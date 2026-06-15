using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

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
		Returns a symbol's headline details; kind, accessibility, signature, and source location,
		resolved by fully-qualified name. If the name is ambiguous the candidate fully-qualified names
		are returned instead. Prefer this over reading the file to identify a symbol; it resolves through
		the compiler, including metadata symbols that have no source to read.
		""")]
	public async Task<GetSymbolResult> GetSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetSymbolResult Success(SymbolDto symbol) =>
			new(model.SnapshotId, model.Status, error: null, symbol);

		GetSymbolResult Failure(Error error) =>
			new(model.SnapshotId, model.Status, error, symbol: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, symbolName);

		if (matches.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(model.Solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}

		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched multiple symbols.", candidates));
		}

		return Success(Map(matches[0], solutionDirectory));
	}

	private static SymbolDto Map(ISymbol symbol, string? solutionDirectory)
	{
		SymbolDocumentation documentation = DocumentationReader.Read(symbol);
		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
		{
			return new SymbolDto(
				name: symbol.Name,
				fullName: SymbolResolver.FullyQualifiedName(symbol),
				kind: symbol.Kind.ToString(),
				accessibility: symbol.DeclaredAccessibility.ToString(),
				signature: symbol.ToDisplayString(),
				sourceType: "metadata",
				assembly: symbol.ContainingAssembly?.Name,
				sourcePath: null,
				startLine: null,
				startColumn: null,
				endLine: null,
				endColumn: null,
				documentation: documentation);
		}

		FileLinePositionSpan span = location.GetLineSpan();
		return new SymbolDto(
			name: symbol.Name,
			fullName: SymbolResolver.FullyQualifiedName(symbol),
			kind: symbol.Kind.ToString(),
			accessibility: symbol.DeclaredAccessibility.ToString(),
			signature: symbol.ToDisplayString(),
			sourceType: "source",
			assembly: null,
			sourcePath: SolutionRelativePath.Of(solutionDirectory, span.Path),
			startLine: span.StartLinePosition.Line + 1,
			startColumn: span.StartLinePosition.Character + 1,
			endLine: span.EndLinePosition.Line + 1,
			endColumn: span.EndLinePosition.Character + 1,
			documentation: documentation);
	}
}
