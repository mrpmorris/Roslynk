using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

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
	public async Task<FindImplementationsResult> FindImplementations(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the interface/abstract symbol, e.g. 'MyNamespace.IMyType'.")] string symbolName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		FindImplementationsResult Success(string resolvedSymbol, IReadOnlyList<string> implementations) =>
			new(model, error: null, resolvedSymbol, implementations);

		FindImplementationsResult Failure(Error error) =>
			new(model, error, resolvedSymbol: null, implementations: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(model.Solution, symbolName);
		if (matches.Count == 0)
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'."));
		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched multiple symbols.", candidates));
		}

		ISymbol symbol = matches[0];
		IEnumerable<ISymbol> implementations = await SymbolFinder.FindImplementationsAsync(symbol, model.Solution);
		string[] names = implementations.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();

		return Success(SymbolResolver.FullyQualifiedName(symbol), names);
	}
}
