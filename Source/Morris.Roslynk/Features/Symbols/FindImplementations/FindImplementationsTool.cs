using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Features.Symbols.FindImplementations;

[McpServerToolType]
public sealed class FindImplementationsTool
{
	public const string FindImplementationsName = "find_implementations";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public FindImplementationsTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = FindImplementationsName,
		Title = "Find implementations",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Finds the implementations or overrides of an interface, interface member, or abstract member,
		resolved by fully-qualified name. Ambiguous names return candidate fully-qualified names instead.
		""")]
	public async Task<FindImplementationsResponse> FindImplementations(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the interface/abstract symbol, e.g. 'MyNamespace.IMyType'.")] string symbolName)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		Solution solution = instance.CurrentSolution;

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(solution, symbolName);
		if (matches.Count == 0)
			return new FindImplementationsResponse(null, [], []);
		if (matches.Count > 1)
			return new FindImplementationsResponse(null, [], matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray());

		ISymbol symbol = matches[0];
		IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution);
		string[] names = implementations.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();

		return new FindImplementationsResponse(SymbolResolver.FullyQualifiedName(symbol), names, []);
	}
}
