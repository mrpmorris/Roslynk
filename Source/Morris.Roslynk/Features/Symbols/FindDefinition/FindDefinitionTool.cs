using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Symbols.FindDefinition;

[McpServerToolType]
public sealed class FindDefinitionTool
{
	public const string FindDefinitionName = "find_definition";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public FindDefinitionTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = FindDefinitionName,
		Title = "Find a symbol's definition",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Resolves the symbol used at a source position (file, 1-based line and column) and returns where it
		is declared — the 'go to definition' jump, by position. Useful when you have a usage site but not
		the symbol's name.
		""")]
	public async Task<FindDefinitionResult> FindDefinition(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Absolute path to the .cs file containing the usage.")] string filePath,
		[Description("1-based line of the usage.")] int line,
		[Description("1-based column of the usage.")] int column)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		FindDefinitionResult Failure(Error error) =>
			new(model.SnapshotId, model.Status, error, fullName: null, kind: null, sourcePath: null, startLine: null, startColumn: null, endLine: null, endColumn: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		ISymbol? symbol = await SymbolResolver.ResolveAtPositionAsync(model.Solution, filePath, line, column);

		if (symbol is null)
			return Failure(Error.NotFound($"No symbol resolved at {filePath} ({line}, {column})."));

		string fullName = SymbolResolver.FullyQualifiedName(symbol);
		string kind = symbol.Kind.ToString();

		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
			return new FindDefinitionResult(
				model.SnapshotId,
				model.Status,
				error: null,
				fullName: fullName,
				kind: kind,
				sourcePath: null,
				startLine: null,
				startColumn: null,
				endLine: null,
				endColumn: null);

		FileLinePositionSpan span = location.GetLineSpan();
		return new FindDefinitionResult(
			model.SnapshotId,
			model.Status,
			error: null,
			fullName: fullName,
			kind: kind,
			sourcePath: span.Path,
			startLine: span.StartLinePosition.Line + 1,
			startColumn: span.StartLinePosition.Character + 1,
			endLine: span.EndLinePosition.Line + 1,
			endColumn: span.EndLinePosition.Character + 1);
	}
}
