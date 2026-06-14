using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Features.Symbols.GetMethod;

[McpServerToolType]
public sealed class GetMethodTool
{
	public const string GetMethodName = "get_method";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public GetMethodTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = GetMethodName,
		Title = "Get method detail",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Returns a method's full detail — return type, accessibility and modifiers, each parameter (type,
		optionality, default, ref kind, params), type parameters, location, and normalized documentation —
		resolved by fully-qualified name. Overloads share a name, so every match is returned as its own
		entry. If the name resolves only to non-methods, their fully-qualified names are returned instead.
		""")]
	public async Task<GetMethodResponse> GetMethod(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the method, e.g. 'MyNamespace.MyType.MyMethod'.")] string methodId)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(instance.CurrentSolution, methodId);

		MethodDto[] methods = matches.OfType<IMethodSymbol>().Select(Map).ToArray();
		if (methods.Length > 0)
			return new GetMethodResponse(methods, []);

		string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
		return new GetMethodResponse([], candidates);
	}

	private static MethodDto Map(IMethodSymbol method)
	{
		Location? location = method.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		FileLinePositionSpan? span = location?.GetLineSpan();

		return new MethodDto(
			Name: method.Name,
			FullName: SymbolResolver.FullyQualifiedName(method),
			Signature: method.ToDisplayString(),
			ReturnType: method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(),
			Accessibility: method.DeclaredAccessibility.ToString(),
			Modifiers: Modifiers(method),
			Parameters: method.Parameters.Select(MapParameter).ToArray(),
			TypeParameters: method.TypeParameters.Select(parameter => parameter.Name).ToArray(),
			SourcePath: span?.Path,
			StartLine: span is { } start ? start.StartLinePosition.Line + 1 : null,
			StartColumn: span is { } startColumn ? startColumn.StartLinePosition.Character + 1 : null,
			EndLine: span is { } end ? end.EndLinePosition.Line + 1 : null,
			EndColumn: span is { } endColumn ? endColumn.EndLinePosition.Character + 1 : null,
			Documentation: DocumentationReader.Read(method));
	}

	private static ParameterDto MapParameter(IParameterSymbol parameter) =>
		new(
			Name: parameter.Name,
			Type: parameter.Type.ToDisplayString(),
			IsOptional: parameter.IsOptional,
			DefaultValue: parameter.HasExplicitDefaultValue ? FormatDefault(parameter.ExplicitDefaultValue) : null,
			RefKind: parameter.RefKind.ToString(),
			IsParams: parameter.IsParams);

	private static IReadOnlyList<string> Modifiers(IMethodSymbol method)
	{
		var modifiers = new List<string>();
		if (method.IsStatic)
			modifiers.Add("static");
		if (method.IsAbstract)
			modifiers.Add("abstract");
		if (method.IsVirtual)
			modifiers.Add("virtual");
		if (method.IsOverride)
			modifiers.Add("override");
		if (method.IsSealed)
			modifiers.Add("sealed");
		if (method.IsAsync)
			modifiers.Add("async");
		if (method.IsExtern)
			modifiers.Add("extern");

		return modifiers;
	}

	private static string FormatDefault(object? value) =>
		value switch
		{
			null => "null",
			string text => $"\"{text}\"",
			bool flag => flag ? "true" : "false",
			_ => value.ToString() ?? "null",
		};
}
