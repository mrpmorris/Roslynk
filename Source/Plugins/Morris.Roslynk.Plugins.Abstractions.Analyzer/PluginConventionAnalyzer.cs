using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Morris.Roslynk.Plugins.Abstractions.Analyzer;

/// <summary>
/// Enforces the Roslynk plugin conventions at build time for plugin entry projects — those whose
/// output assembly name is {Name}.Roslynk.Plugin. That suffix is the gate: projects with any other
/// assembly name are not plugin entry projects and are never analyzed; the naming rule then
/// validates the {Name} prefix against the plugin-name rules, which is how a misnamed {Name} is
/// caught at build time.
///
/// Factory shape is deliberately not an analyzer rule: the static abstract member on
/// IPluginFactory makes a wrong factory a compile error in the plugin author's build, and the
/// compiler enforces nothing about the factory class itself (it may be internal or generic).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PluginConventionAnalyzer : DiagnosticAnalyzer
{
	private const string ContractNamespace = "Morris.Roslynk.Plugins.Abstractions";
	private const string PluginAssemblySuffix = ".Roslynk.Plugin";
	private const string McpServerToolAttributeName = "ModelContextProtocol.Server.McpServerToolAttribute";
	private const string CancellationTokenTypeName = "System.Threading.CancellationToken";
	private const string SolutionIdParameterName = "solutionId";

	/// <summary>
	/// The plugin-name rules, carried here in full because the analyzer must run inside any
	/// IDE/SDK that compiles a plugin, where the host assembly is absent. Keep in step with
	/// Morris.Roslynk.Infrastructure.Plugins.PluginNameRules when the rules change.
	/// </summary>
	private static readonly Regex PluginNameRegex = new("^[A-Za-z][A-Za-z0-9]{2,31}$", RegexOptions.Compiled);

	private static readonly DiagnosticDescriptor MissingFactoryAttribute = new(
		"RKPL001",
		"Plugin entry project has no [assembly: PluginFactory<...>] attribute",
		"The plugin entry project '{0}' does not declare [assembly: PluginFactory<FactoryType>] where FactoryType implements IPluginFactory",
		"Roslynk.PluginContract",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "Nothing in the language forces the factory claim's presence; the assembly-level PluginFactory<T> attribute is the plugin's factory claim.",
		customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

	private static readonly DiagnosticDescriptor MultipleFactoryAttributes = new(
		"RKPL002",
		"More than one PluginFactory<> application",
		"The plugin entry project '{0}' declares {1} PluginFactory<> applications; exactly one is required",
		"Roslynk.PluginContract",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "Distinct closed generic types each satisfy AllowMultiple = false, so more than one factory claim needs an explicit rule.",
		customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

	private static readonly DiagnosticDescriptor PluginAssemblyNameMismatch = new(
		"RKPL003",
		"Output assembly name must be {Name}.Roslynk.Plugin",
		"The output assembly name '{0}' does not match '{{Name}}.Roslynk.Plugin' where {{Name}} matches the plugin-name rules (3-32 characters, a letter first, then letters or digits)",
		"Roslynk.PluginContract",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "The plugin name, the entry assembly's base name and the containing deployment folder are all {Name} — one rule everywhere.",
		customTags: new[] { WellKnownDiagnosticTags.CompilationEnd });

	private static readonly DiagnosticDescriptor PluginToolSignatureInvalid = new(
		"RKPL004",
		"Plugin tool signature is invalid",
		"The plugin tool '{0}' must declare 'string solutionId' as its first parameter and have a CancellationToken parameter",
		"Roslynk.PluginContract",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		description: "Every plugin tool declares string solutionId first and a CancellationToken parameter; the token the host passes is the request's cancellation token linked with the host-shutdown token.");

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
		ImmutableArray.Create(MissingFactoryAttribute, MultipleFactoryAttributes, PluginAssemblyNameMismatch, PluginToolSignatureInvalid);

	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterCompilationStartAction(OnCompilationStart);
	}

	private static void OnCompilationStart(CompilationStartAnalysisContext context)
	{
		string assemblyName = context.Compilation.Assembly.Name;
		if (!assemblyName.EndsWith(PluginAssemblySuffix, StringComparison.Ordinal))
			return;

		string candidateName = assemblyName.Substring(0, assemblyName.Length - PluginAssemblySuffix.Length);
		if (!PluginNameRegex.IsMatch(candidateName))
		{
			// The assembly declares itself a plugin entry but {Name} breaks the plugin-name rules.
			// Every other check would be noise on top of this one.
			context.RegisterCompilationEndAction(endContext =>
				endContext.ReportDiagnostic(Diagnostic.Create(PluginAssemblyNameMismatch, Location.None, assemblyName)));
			return;
		}

		context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
		context.RegisterCompilationEndAction(endContext => OnCompilationEnd(endContext, assemblyName));
	}

	private static void AnalyzeNamedType(SymbolAnalysisContext context)
	{
		var namedType = (INamedTypeSymbol)context.Symbol;
		if (namedType.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface))
			return;

		foreach (IMethodSymbol method in namedType.GetMembers().OfType<IMethodSymbol>())
		{
			if (!HasToolAttribute(method))
				continue;

			if (!IsToolSignatureValid(method))
				context.ReportDiagnostic(Diagnostic.Create(PluginToolSignatureInvalid, GetLocation(method), method.Name));
		}
	}

	private static void OnCompilationEnd(CompilationAnalysisContext context, string assemblyName)
	{
		int factoryCount = context.Compilation.Assembly
			.GetAttributes()
			.Count(attribute => IsFactoryAttribute(attribute.AttributeClass));

		if (factoryCount == 0)
			context.ReportDiagnostic(Diagnostic.Create(MissingFactoryAttribute, Location.None, assemblyName));
		else if (factoryCount > 1)
			context.ReportDiagnostic(Diagnostic.Create(MultipleFactoryAttributes, Location.None, assemblyName, factoryCount));
	}

	private static bool IsToolSignatureValid(IMethodSymbol method)
	{
		if (method.Parameters.Length == 0 || !IsStringSolutionIdParameter(method.Parameters[0]))
			return false;

		return method.Parameters.Any(parameter => parameter.Type.ToDisplayString() == CancellationTokenTypeName);
	}

	private static bool IsStringSolutionIdParameter(IParameterSymbol parameter) =>
		parameter.Name == SolutionIdParameterName && parameter.Type.SpecialType == SpecialType.System_String;

	private static bool HasToolAttribute(IMethodSymbol method) =>
		method.GetAttributes().Any(attribute => IsNamedType(attribute.AttributeClass, McpServerToolAttributeName));

	private static bool IsFactoryAttribute(ITypeSymbol? type)
	{
		if (type is not INamedTypeSymbol namedType || !namedType.IsGenericType)
			return false;

		INamedTypeSymbol openGeneric = namedType.ConstructedFrom;
		return openGeneric.MetadataName == "PluginFactoryAttribute`1"
			&& openGeneric.ContainingNamespace.ToDisplayString() == ContractNamespace;
	}

	private static bool IsNamedType(ITypeSymbol? type, string fullyQualifiedName) =>
		type is INamedTypeSymbol namedType && namedType.ToDisplayString() == fullyQualifiedName;

	private static Location GetLocation(IMethodSymbol method) =>
		method.Locations.IsEmpty ? Location.None : method.Locations[0];
}
