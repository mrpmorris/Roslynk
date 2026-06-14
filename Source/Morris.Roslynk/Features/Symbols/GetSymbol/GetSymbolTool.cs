using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

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
	public async Task<GetSymbolResult> GetSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetSymbolResult Success(SymbolDto symbol) =>
			new() { SnapshotId = model.SnapshotId, Status = model.Status, Symbol = symbol };

		GetSymbolResult Failure(Error error) =>
			new() { SnapshotId = model.SnapshotId, Status = model.Status, Error = error };

		if (model.Solution is null)
			return Failure(Error.Indexing());

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

		return Success(Map(matches[0]));
	}

	private static SymbolDto Map(ISymbol symbol)
	{
		SymbolDocumentation documentation = DocumentationReader.Read(symbol);
		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
		{
			return new SymbolDto(
				Name: symbol.Name,
				FullName: SymbolResolver.FullyQualifiedName(symbol),
				Kind: symbol.Kind.ToString(),
				Accessibility: symbol.DeclaredAccessibility.ToString(),
				Signature: symbol.ToDisplayString(),
				SourceType: "metadata",
				Assembly: symbol.ContainingAssembly?.Name,
				SourcePath: null,
				StartLine: null,
				StartColumn: null,
				EndLine: null,
				EndColumn: null,
				Documentation: documentation);
		}

		FileLinePositionSpan span = location.GetLineSpan();
		return new SymbolDto(
			Name: symbol.Name,
			FullName: SymbolResolver.FullyQualifiedName(symbol),
			Kind: symbol.Kind.ToString(),
			Accessibility: symbol.DeclaredAccessibility.ToString(),
			Signature: symbol.ToDisplayString(),
			SourceType: "source",
			Assembly: null,
			SourcePath: span.Path,
			StartLine: span.StartLinePosition.Line + 1,
			StartColumn: span.StartLinePosition.Character + 1,
			EndLine: span.EndLinePosition.Line + 1,
			EndColumn: span.EndLinePosition.Character + 1,
			Documentation: documentation);
	}
}
