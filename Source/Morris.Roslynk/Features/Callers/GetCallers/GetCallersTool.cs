using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

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
		$"""
		Finds the methods that call the resolved method (by fully-qualified name). {OutlineDescriptions.TextNotJson}
		Callers are grouped file -> namespace -> containing type -> calling member, each leaf showing the
		caller's declaration location:
		  #resolvedSymbol=<fully-qualified name>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>
		where kind is one of {OutlineDescriptions.KindList} and {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}.
		{OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over grepping for call sites; it resolves the actual
		method through the compiler, so overloads and same-named methods are not confused.
		""")]
	public async Task<string> GetCallers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the method, e.g. 'MyNamespace.MyType.MyMethod'.")] string methodName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

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
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		List<ISymbol> callers = (await SymbolFinder.FindCallersAsync(symbol, model.Solution))
			.Select(caller => caller.CallingSymbol)
			.DistinctBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.ToList();

		var root = new SymbolNode();
		foreach (ISymbol caller in callers)
			SymbolPlacement.Place(root, caller, model.Solution, solutionDirectory);

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", SymbolResolver.FullyQualifiedName(symbol));
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
