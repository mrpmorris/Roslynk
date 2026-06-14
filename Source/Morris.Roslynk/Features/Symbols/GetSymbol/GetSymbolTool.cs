using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Features.Symbols.GetSymbol;

[McpServerToolType]
public sealed class GetSymbolTool
{
	public const string GetSymbolName = "get_symbol";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public GetSymbolTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = GetSymbolName,
		Title = "Get symbol details",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Returns a symbol's headline details — kind, accessibility, signature, and source location —
		resolved by fully-qualified name. If the name is ambiguous the candidate fully-qualified names
		are returned instead.
		""")]
	public async Task<GetSymbolResponse> GetSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(instance.CurrentSolution, symbolName);

		if (matches.Count == 0)
			return new GetSymbolResponse(Symbol: null, Candidates: []);

		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return new GetSymbolResponse(Symbol: null, Candidates: candidates);
		}

		return new GetSymbolResponse(Map(matches[0]), Candidates: []);
	}

	private static SymbolDto Map(ISymbol symbol)
	{
		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
		{
			return new SymbolDto(
				Name: symbol.Name,
				FullName: SymbolResolver.FullyQualifiedName(symbol),
				Kind: symbol.Kind.ToString(),
				Accessibility: symbol.DeclaredAccessibility.ToString(),
				Signature: symbol.ToDisplayString(),
				SourcePath: null,
				StartLine: null,
				StartColumn: null,
				EndLine: null,
				EndColumn: null);
		}

		FileLinePositionSpan span = location.GetLineSpan();
		return new SymbolDto(
			Name: symbol.Name,
			FullName: SymbolResolver.FullyQualifiedName(symbol),
			Kind: symbol.Kind.ToString(),
			Accessibility: symbol.DeclaredAccessibility.ToString(),
			Signature: symbol.ToDisplayString(),
			SourcePath: span.Path,
			StartLine: span.StartLinePosition.Line + 1,
			StartColumn: span.StartLinePosition.Character + 1,
			EndLine: span.EndLinePosition.Line + 1,
			EndColumn: span.EndLinePosition.Character + 1);
	}
}
