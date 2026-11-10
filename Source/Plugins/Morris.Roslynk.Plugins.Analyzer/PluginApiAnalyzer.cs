using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Morris.Roslynk.Plugins.V1.Analyzer;

/// <summary>
/// Enforces the Roslynk plugin conventions at build time for plugin entry projects — those whose
/// output assembly name is {Name}.Roslynk.Plugin. Projects with any other assembly name are not
/// plugin entry projects and are never analyzed; a wrongly named assembly is caught when it is
/// deployed, by `roslynk plugin add` and by the loader.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PluginApiAnalyzer : DiagnosticAnalyzer
{
	private const string ContractNamespace = "Morris.Roslynk.Plugins.V1";
	private const string PluginAssemblySuffix = ".Roslynk.Plugin";
	private const string FactoryAttributeName = ContractNamespace + ".RoslynkPluginFactoryAttribute";
	private const string ApiVersionAttributeName = ContractNamespace + ".RoslynkApiVersionAttribute";
	private const string McpServerToolAttributeName = "ModelContextProtocol.Server.McpServerToolAttribute";
	private const string PluginContextTypeName = ContractNamespace + ".IPluginContext";
	private const string RoslynkPluginTypeName = ContractNamespace + ".IRoslynkPlugin";
	private const string CancellationTokenTypeName = "System.Threading.CancellationToken";
	private const string SolutionIdParameterName = "solutionId";
	private const int MaxPluginNameLength = 32;
	private const int MaxExposedNameLength = 64;

	private static readonly Regex PluginNameRegex = new("^[A-Za-z][A-Za-z0-9]{2,31}$", RegexOptions.Compiled);
	private static readonly Regex ToolNameCharsetRegex = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

	private static readonly DiagnosticDescriptor MissingPluginFactory = new(
		"RK0001",
		"Plugin entry project has no [RoslynkPluginFactory] factory",
		"The plugin entry project '{0}' does not declare exactly one [RoslynkPluginFactory] static method",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		customTags: new[] { "CompilationEnd" });

	private static readonly DiagnosticDescriptor MultiplePluginFactories = new(
		"RK0002",
		"Multiple [RoslynkPluginFactory] methods",
		"The plugin entry project declares {0} [RoslynkPluginFactory] methods; exactly one is required",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		customTags: new[] { "CompilationEnd" });

	private static readonly DiagnosticDescriptor FactoryNotPubliclyAccessible = new(
		"RK0003",
		"Factory is not declared in a publicly accessible class",
		"The [RoslynkPluginFactory] method '{0}' must be declared in a public class, with public containing types",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor FactoryNotPublicOrStatic = new(
		"RK0004",
		"Factory must be public and static",
		"The [RoslynkPluginFactory] method '{0}' must be public and static",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor FactoryOrDeclaringTypeGeneric = new(
		"RK0005",
		"Factory and declaring type must not be generic",
		"The [RoslynkPluginFactory] method '{0}' and its declaring type must not be generic",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor FactoryIncorrectParameters = new(
		"RK0006",
		"Factory must accept exactly one IPluginContext parameter",
		"The [RoslynkPluginFactory] method '{0}' must accept exactly one parameter of type " + PluginContextTypeName,
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor FactoryIncorrectReturnType = new(
		"RK0007",
		"Factory must return nullable IRoslynkPlugin",
		"The [RoslynkPluginFactory] method '{0}' must return nullable " + RoslynkPluginTypeName,
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor PluginAssemblyNameMismatch = new(
		"RK0008",
		"Output assembly name must be {Name}.Roslynk.Plugin",
		"The output assembly name '{0}' does not match '{{Name}}.Roslynk.Plugin' where {{Name}} is a valid plugin name",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		customTags: new[] { "CompilationEnd" });

	private static readonly DiagnosticDescriptor PluginToolNameInvalid = new(
		"RK0009",
		"Exposed plugin tool name is invalid",
		"The exposed tool name '{1}' for plugin tool '{0}' exceeds 64 characters or contains characters outside letters, digits, underscore and hyphen",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor PluginToolFirstParameterNotSolutionId = new(
		"RK0010",
		"Plugin tool must take string solutionId first",
		"The plugin tool '{0}' must declare 'string solutionId' as its first parameter",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor PluginToolMissingCancellationToken = new(
		"RK0011",
		"Plugin tool must accept a CancellationToken",
		"The plugin tool '{0}' must declare a CancellationToken parameter",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor PluginToolNameContainsDoubleUnderscore = new(
		"RK0012",
		"Plugin tool name must not contain '__'",
		"The plugin tool '{0}' must not contain '__'; the separator would collide with the plugin name in exposed tool names",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor MissingApiVersionAttribute = new(
		"RK0013",
		"Missing RoslynkApiVersion assembly attribute",
		"The plugin entry project '{0}' must be stamped with [RoslynkApiVersion(...)]; the PluginAPI package build targets add it automatically",
		"Roslynk.PluginApi",
		DiagnosticSeverity.Error,
		isEnabledByDefault: true,
		customTags: new[] { "CompilationEnd" });

	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; }

	public PluginApiAnalyzer()
	{
		SupportedDiagnostics = ImmutableArray.Create(
			MissingPluginFactory,
			MultiplePluginFactories,
			FactoryNotPubliclyAccessible,
			FactoryNotPublicOrStatic,
			FactoryOrDeclaringTypeGeneric,
			FactoryIncorrectParameters,
			FactoryIncorrectReturnType,
			PluginAssemblyNameMismatch,
			PluginToolNameInvalid,
			PluginToolFirstParameterNotSolutionId,
			PluginToolMissingCancellationToken,
			PluginToolNameContainsDoubleUnderscore,
			MissingApiVersionAttribute);
	}

	public override void Initialize(AnalysisContext context)
	{
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.EnableConcurrentExecution();

		context.RegisterCompilationStartAction(OnCompilationStart);
	}

	private static void OnCompilationStart(CompilationStartAnalysisContext context)
	{
		string assemblyName = context.Compilation.Assembly.Name;
		if (!TryGetPluginName(assemblyName, out string? pluginName))
			return;

		var factories = new ConcurrentQueue<IMethodSymbol>();

		if (pluginName is null)
		{
			// The assembly declares itself a plugin entry ({Name}.Roslynk.Plugin) but {Name} breaks
			// the plugin-name rules. Every other check would be noise on top of this one.
			context.RegisterCompilationEndAction(endContext =>
				endContext.ReportDiagnostic(Diagnostic.Create(PluginAssemblyNameMismatch, Location.None, assemblyName)));
			return;
		}

		context.RegisterSymbolAction(symbolContext => AnalyzeNamedType(symbolContext, pluginName, factories), SymbolKind.NamedType);
		context.RegisterCompilationEndAction(endContext => OnCompilationEnd(endContext, assemblyName, factories));
	}

	private static void AnalyzeNamedType(SymbolAnalysisContext context, string pluginName, ConcurrentQueue<IMethodSymbol> factories)
	{
		var namedType = (INamedTypeSymbol)context.Symbol;

		foreach (IMethodSymbol method in namedType.GetMembers().OfType<IMethodSymbol>())
		{
			bool hasFactoryAttribute = HasAttribute(method, FactoryAttributeName);
			bool hasToolAttribute = HasAttribute(method, McpServerToolAttributeName);
			if (!hasFactoryAttribute && !hasToolAttribute)
				continue;

			Location location = GetLocation(method);

			if (hasFactoryAttribute)
			{
				factories.Enqueue(method);
				ValidateFactory(context, method, location);
			}

			if (hasToolAttribute)
				ValidateTool(context, method, pluginName, location);
		}
	}

	private static void OnCompilationEnd(CompilationAnalysisContext context, string assemblyName, ConcurrentQueue<IMethodSymbol> factories)
	{
		if (!HasApiVersionAttribute(context.Compilation))
			context.ReportDiagnostic(Diagnostic.Create(MissingApiVersionAttribute, Location.None, assemblyName));

		if (factories.IsEmpty)
		{
			context.ReportDiagnostic(Diagnostic.Create(MissingPluginFactory, Location.None, assemblyName));
			return;
		}

		foreach (IMethodSymbol extraFactory in factories.Skip(1))
			context.ReportDiagnostic(Diagnostic.Create(MultiplePluginFactories, GetLocation(extraFactory), factories.Count));
	}

	private static void ValidateFactory(SymbolAnalysisContext context, IMethodSymbol method, Location location)
	{
		if (!IsPubliclyAccessibleType(method.ContainingType))
			Report(context, FactoryNotPubliclyAccessible, location, method.Name);

		if (method.DeclaredAccessibility != Accessibility.Public || !method.IsStatic)
			Report(context, FactoryNotPublicOrStatic, location, method.Name);

		if (method.IsGenericMethod || method.ContainingType.IsGenericType)
			Report(context, FactoryOrDeclaringTypeGeneric, location, method.Name);

		if (method.Parameters.Length != 1 || !IsContractType(method.Parameters[0].Type, PluginContextTypeName))
			Report(context, FactoryIncorrectParameters, location, method.Name);

		if (!IsNullableContractType(method.ReturnType, RoslynkPluginTypeName))
			Report(context, FactoryIncorrectReturnType, location, method.Name);
	}

	private static void ValidateTool(SymbolAnalysisContext context, IMethodSymbol method, string? pluginName, Location location)
	{
		if (method.Parameters.Length == 0 || !IsStringSolutionIdParameter(method.Parameters[0]))
			Report(context, PluginToolFirstParameterNotSolutionId, location, method.Name);

		if (!method.Parameters.Any(parameter => parameter.Type.ToDisplayString() == CancellationTokenTypeName))
			Report(context, PluginToolMissingCancellationToken, location, method.Name);

		if (method.Name.Contains("__", StringComparison.Ordinal))
			Report(context, PluginToolNameContainsDoubleUnderscore, location, method.Name);

		// When {Name} is invalid the assembly-name rule has already been reported once; do not pile
		// exposed-name noise on top of it.
		if (pluginName is null)
			return;

		string exposedName = pluginName + "__" + method.Name;
		if (exposedName.Length > MaxExposedNameLength || !ToolNameCharsetRegex.IsMatch(method.Name))
			Report(context, PluginToolNameInvalid, location, method.Name, exposedName);
	}

	private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor descriptor, Location location, params object?[] messageArguments) =>
		context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArguments));

	private static bool TryGetPluginName(string assemblyName, out string? pluginName)
	{
		pluginName = null;
		if (!assemblyName.EndsWith(PluginAssemblySuffix, StringComparison.Ordinal))
			return false;

		string candidate = assemblyName.Substring(0, assemblyName.Length - PluginAssemblySuffix.Length);
		if (candidate.Length is < 3 or > MaxPluginNameLength)
			return true;

		pluginName = PluginNameRegex.IsMatch(candidate) ? candidate : null;
		return true;
	}

	private static bool HasAttribute(IMethodSymbol method, string attributeTypeName) =>
		method.GetAttributes().Any(attribute => IsNamedType(attribute.AttributeClass, attributeTypeName));

	private static bool HasApiVersionAttribute(Compilation compilation) =>
		compilation.Assembly.GetAttributes().Any(attribute => IsNamedType(attribute.AttributeClass, ApiVersionAttributeName));

	private static bool IsNamedType(ITypeSymbol? type, string fullyQualifiedName) =>
		type is INamedTypeSymbol namedType && namedType.ToDisplayString() == fullyQualifiedName;

	private static bool IsContractType(ITypeSymbol type, string fullyQualifiedName) =>
		IsNamedType(type, fullyQualifiedName);

	private static bool IsNullableContractType(ITypeSymbol type, string fullyQualifiedName) =>
		type.NullableAnnotation == NullableAnnotation.Annotated
		&& type.WithNullableAnnotation(NullableAnnotation.NotAnnotated) is INamedTypeSymbol namedType
		&& namedType.ToDisplayString() == fullyQualifiedName;

	private static bool IsStringSolutionIdParameter(IParameterSymbol parameter) =>
		parameter.Name == SolutionIdParameterName && parameter.Type.SpecialType == SpecialType.System_String;

	private static bool IsPubliclyAccessibleType(INamedTypeSymbol type)
	{
		for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
		{
			if (current.DeclaredAccessibility != Accessibility.Public)
				return false;
		}

		return true;
	}

	private static Location GetLocation(IMethodSymbol method) =>
		method.Locations.IsEmpty ? Location.None : method.Locations[0];
}
