using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Callers.GetCallers;

[McpServerToolType]
public sealed class GetCallersTool
{
	public const string GetCallersName = "get_callers";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public GetCallersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = GetCallersName,
		Title = "Get callers of a method",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Finds the methods that call the resolved method (by fully-qualified name). Ambiguous names return
		candidate fully-qualified names instead.
		""")]
	public async Task<GetCallersResult> GetCallers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the method, e.g. 'MyNamespace.MyType.MyMethod'.")] string methodName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetCallersResult Success(string resolvedSymbol, IReadOnlyList<string> callers) =>
			new() { SnapshotId = model.SnapshotId, Status = model.Status, ResolvedSymbol = resolvedSymbol, Callers = callers };

		GetCallersResult Failure(Error error) =>
			new() { SnapshotId = model.SnapshotId, Status = model.Status, Error = error };

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(model.Solution, methodName);

		if (matches.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(model.Solution, methodName);
			return Failure(Error.NotFound($"No symbol matched '{methodName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{methodName}' matched several symbols.", candidates));
		}

		ISymbol symbol = matches[0];
		IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(symbol, model.Solution);
		string[] callerNames = callers
			.Select(caller => SymbolResolver.FullyQualifiedName(caller.CallingSymbol))
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		return Success(SymbolResolver.FullyQualifiedName(symbol), callerNames);
	}
}
