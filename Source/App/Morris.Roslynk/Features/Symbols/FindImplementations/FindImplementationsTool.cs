using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
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
	private readonly ProjectionService ProjectionService;

	public FindImplementationsTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
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

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>,<loc>
		{OutlineDescriptions.ListFieldQuoting} {OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over reading files to find implementors; it walks the
		compiler's type graph, not text.
		""")]
	public async Task<string> FindImplementations(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the interface/abstract symbol, e.g. 'MyNamespace.IMyType'.")] string symbolName)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = await instance.ReadModelAsync();

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName);
		if (groups.Count == 0)
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'."));
		if (groups.Count > 1)
		{
			string[] candidates = groups.Select(group => SymbolResolver.FullyQualifiedName(group[0].Symbol)).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched multiple symbols.", candidates));
		}

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union implementors across every projection (per-TFM + #if-toggle), deduped by stable symbol identity,
		// so implementors declared in a branch that is inactive in the loaded configuration are still listed.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var root = new SymbolNode();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ISymbol implementation in await SymbolFinder.FindImplementationsAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution))
			{
				if (seen.Add(ProjectionService.KeyOf(implementation)))
					SymbolPlacement.Place(root, implementation, projectionSymbol.Projection.Solution, solutionDirectory);
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", SymbolResolver.FullyQualifiedName(resolved[0].Symbol));
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
