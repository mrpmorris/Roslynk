using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

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
		Pass includeBody to also get each method's source body.
		""")]
	public async Task<GetMethodResult> GetMethod(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the method, e.g. 'MyNamespace.MyType.MyMethod'.")] string methodId,
		[Description("When true, also return each method's source body (block or expression body). Defaults to false; null for methods that have no body, such as abstract, interface, or metadata methods.")] bool includeBody = false)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetMethodResult Success(IReadOnlyList<MethodDto> methods) => new(model.SnapshotId, model.Status, error: null, methods);

		GetMethodResult Failure(Error error) => new(model.SnapshotId, model.Status, error, methods: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, methodId);

		MethodDto[] methods = matches.OfType<IMethodSymbol>().Select(method => Map(method, includeBody)).ToArray();
		if (methods.Length > 0)
			return Success(methods);

		string[] resolved = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
		IReadOnlyList<string> candidates = resolved.Length > 0
			? resolved
			: await SymbolResolver.SuggestAsync(model.Solution, methodId);

		return Failure(Error.NotFound($"No method matched '{methodId}'.", candidates.Count > 0 ? candidates : null));
	}

	private static MethodDto Map(IMethodSymbol method, bool includeBody)
	{
		Location? location = method.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		FileLinePositionSpan? span = location?.GetLineSpan();

		return new MethodDto(
			name: method.Name,
			fullName: SymbolResolver.FullyQualifiedName(method),
			signature: method.ToDisplayString(),
			returnType: method.ReturnsVoid ? "void" : method.ReturnType.ToDisplayString(),
			accessibility: method.DeclaredAccessibility.ToString(),
			modifiers: Modifiers(method),
			parameters: method.Parameters.Select(MapParameter).ToArray(),
			typeParameters: method.TypeParameters.Select(parameter => parameter.Name).ToArray(),
			sourcePath: span?.Path,
			startLine: span is { } start ? start.StartLinePosition.Line + 1 : null,
			startColumn: span is { } startColumn ? startColumn.StartLinePosition.Character + 1 : null,
			endLine: span is { } end ? end.EndLinePosition.Line + 1 : null,
			endColumn: span is { } endColumn ? endColumn.EndLinePosition.Character + 1 : null,
			body: includeBody ? ReadBody(method) : null,
			documentation: DocumentationReader.Read(method));
	}

	private static string? ReadBody(IMethodSymbol method)
	{
		foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
		{
			if (reference.GetSyntax() is not BaseMethodDeclarationSyntax declaration)
				continue;

			if (declaration.Body is { } block)
				return block.ToString();

			if (declaration.ExpressionBody is { } expressionBody)
				return expressionBody.ToString();
		}

		return null;
	}

	private static ParameterDto MapParameter(IParameterSymbol parameter) =>
		new(
			name: parameter.Name,
			type: parameter.Type.ToDisplayString(),
			isOptional: parameter.IsOptional,
			defaultValue: parameter.HasExplicitDefaultValue ? FormatDefault(parameter.ExplicitDefaultValue) : null,
			refKind: parameter.RefKind.ToString(),
			isParams: parameter.IsParams);

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
