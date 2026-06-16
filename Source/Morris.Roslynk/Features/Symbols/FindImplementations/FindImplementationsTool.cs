using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

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
		$"""
		Finds the implementations or overrides of an interface, interface member, or abstract member, resolved
		by fully-qualified name. {OutlineDescriptions.TextNotJson} Implementors are grouped file -> namespace,
		each as '<typeKind>,<typeName>,<loc>' where {OutlineDescriptions.Loc}:
		  #resolvedSymbol=<fully-qualified name>

		  <relative/forward-slash/path.cs>
		  \t<namespace>
		  \t\t<typeKind>,<typeName>,<loc>
		{OutlineDescriptions.ListFieldQuoting} {OutlineDescriptions.ErrorBlock} Prefer this over reading files to find implementors; it walks the
		compiler's type graph, not text.
		""")]
	public async Task<string> FindImplementations(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the interface/abstract symbol, e.g. 'MyNamespace.IMyType'.")] string symbolName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

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
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		List<ISymbol> implementations = (await SymbolFinder.FindImplementationsAsync(symbol, model.Solution))
			.DistinctBy(SymbolResolver.FullyQualifiedName, StringComparer.Ordinal)
			.ToList();

		var root = new SymbolNode();
		foreach (ISymbol implementation in implementations)
			SymbolPlacement.Place(root, implementation, solutionDirectory);

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", SymbolResolver.FullyQualifiedName(symbol));
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
