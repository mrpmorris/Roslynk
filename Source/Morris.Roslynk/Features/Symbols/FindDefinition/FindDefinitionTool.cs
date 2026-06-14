using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

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
	public async Task<FindDefinitionResponse> FindDefinition(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Absolute path to the .cs file containing the usage.")] string filePath,
		[Description("1-based line of the usage.")] int line,
		[Description("1-based column of the usage.")] int column)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		ISymbol? symbol = await SymbolResolver.ResolveAtPositionAsync(instance.CurrentSolution, filePath, line, column);

		if (symbol is null)
			return new FindDefinitionResponse(null, null, null, null, null, null, null);

		string fullName = SymbolResolver.FullyQualifiedName(symbol);
		string kind = symbol.Kind.ToString();

		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
			return new FindDefinitionResponse(fullName, kind, null, null, null, null, null);

		FileLinePositionSpan span = location.GetLineSpan();
		return new FindDefinitionResponse(
			fullName,
			kind,
			span.Path,
			span.StartLinePosition.Line + 1,
			span.StartLinePosition.Character + 1,
			span.EndLinePosition.Line + 1,
			span.EndLinePosition.Character + 1);
	}
}
