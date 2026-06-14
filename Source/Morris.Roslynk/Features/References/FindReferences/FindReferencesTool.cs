using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.References.FindReferences;

[McpServerToolType]
public sealed class FindReferencesTool
{
	public const string FindReferencesName = "find_references";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public FindReferencesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = FindReferencesName,
		Title = "Find references to a symbol",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Finds all references to a symbol across the solution, resolved by fully-qualified name (e.g.
		'Namespace.Type' or 'Namespace.Type.Member'). If the name resolves to more than one symbol the
		candidate fully-qualified names are returned instead, so you can disambiguate.
		""")]
	public async Task<FindReferencesResult> FindReferences(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName,
		[Description("Maximum reference locations to return. Default 100.")] int maxResults = 100)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		FindReferencesResult Success(string resolvedSymbol, IReadOnlyList<ReferenceDto> references, bool truncated) =>
			new(model, error: null, resolvedSymbol, references, truncated);

		FindReferencesResult Failure(Error error) =>
			new(model, error, resolvedSymbol: null, references: null, truncated: false);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(model.Solution, symbolName);

		if (matches.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(model.Solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(match => match.ToDisplayString()).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched several symbols.", candidates));
		}

		ISymbol symbol = matches[0];
		var references = new List<ReferenceDto>();
		foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, model.Solution))
		{
			foreach (ReferenceLocation reference in referenced.Locations)
			{
				if (reference.Location.IsInSource)
					references.Add(Map(reference.Location));
			}
		}

		ReferenceDto[] page = references.Take(Math.Max(0, maxResults)).ToArray();
		return Success(symbol.ToDisplayString(), page, truncated: references.Count > page.Length);
	}

	private static ReferenceDto Map(Location location)
	{
		FileLinePositionSpan span = location.GetLineSpan();
		return new ReferenceDto(
			SourcePath: span.Path,
			StartLine: span.StartLinePosition.Line + 1,
			StartColumn: span.StartLinePosition.Character + 1,
			EndLine: span.EndLinePosition.Line + 1,
			EndColumn: span.EndLinePosition.Character + 1);
	}
}
