using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

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
	public async Task<GetCallersResponse> GetCallers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the method, e.g. 'MyNamespace.MyType.MyMethod'.")] string methodName)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		Solution solution = instance.CurrentSolution;

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(solution, methodName);
		if (matches.Count == 0)
			return new GetCallersResponse(null, [], []);
		if (matches.Count > 1)
			return new GetCallersResponse(null, [], matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray());

		ISymbol symbol = matches[0];
		IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(symbol, solution);
		string[] callerNames = callers
			.Select(caller => SymbolResolver.FullyQualifiedName(caller.CallingSymbol))
			.Distinct(StringComparer.Ordinal)
			.ToArray();

		return new GetCallersResponse(SymbolResolver.FullyQualifiedName(symbol), callerNames, []);
	}
}
