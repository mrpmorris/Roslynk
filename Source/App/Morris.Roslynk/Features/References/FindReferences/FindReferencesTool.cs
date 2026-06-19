using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.References.FindReferences;

[McpServerToolType]
public sealed class FindReferencesTool
{
	public const string FindReferencesName = "find_references";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;

	public FindReferencesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = FindReferencesName,
		Title = "Find references to a symbol",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Finds all references to a symbol across the solution, resolved by fully-qualified name (e.g.
		'Namespace.Type' or 'Namespace.Type.Member'). {OutlineDescriptions.TextNotJson} The body groups every
		reference under file -> namespace -> type(s) -> member, so a shared declaration is printed once:
		  #resolvedSymbol=<fully-qualified name>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace, or "<global>">
		  \t\t\t\t<typeKind>,<typeName>,<loc|loc|...>   (locations present only when the type declaration itself references the symbol)
		  \t\t\t\t\t<memberKind>,<memberName>,<loc|loc|...>
		where kind is one of {OutlineDescriptions.KindList}; {OutlineDescriptions.Loc}; {OutlineDescriptions.LocList}; {OutlineDescriptions.ListFieldQuoting}.
		{OutlineDescriptions.Truncation} {OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over grepping: it matches the compiler's symbol, not text,
		so it skips comments, strings and unrelated same-named members, and still finds usages in code-behind
		and partial classes.
		""")]
	public async Task<string> FindReferences(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName,
		[Description("Maximum reference locations to return. Default 100.")] int maxResults = 100,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		// Run against every projection (per-TFM projects + #if-toggle variants) so references in conditionally
		// compiled branches that are inactive in the loaded configuration are still found.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(solution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName, cancellationToken);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
		{
			string[] candidates = groups.Select(group => group[0].Symbol.ToDisplayString()).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched several symbols.", candidates));
		}

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		ISymbol representative = resolved[0].Symbol;

		// Union references from each projection instance of the symbol, deduped by file + span so a reference
		// in shared (non-conditional) code is reported once even though it is seen in several projections.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hits = new List<(Location Location, Solution Solution)>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution, cancellationToken))
			{
				foreach (ReferenceLocation reference in referenced.Locations)
				{
					if (!reference.Location.IsInSource)
						continue;

					FileLinePositionSpan referenceSpan = reference.Location.GetDisplaySpan();
					string key = $"{referenceSpan.Path}|{referenceSpan.StartLinePosition}-{referenceSpan.EndLinePosition}";
					if (seen.Add(key))
						hits.Add((reference.Location, projectionSymbol.Projection.Solution));
				}
			}
		}

		List<(Location Location, Solution Solution)> page = hits.Take(Math.Max(0, maxResults)).ToList();
		bool truncated = hits.Count > page.Count;

		var root = new SymbolNode();
		foreach ((Location location, Solution locationSolution) in page)
		{
			EnclosingPath enclosing = await EnclosingDeclaration.ResolveAsync(locationSolution, location, cancellationToken);
			FileLinePositionSpan span = location.GetDisplaySpan();

			SymbolNode fileParent = location.SourceTree is SyntaxTree tree && ProjectName.Of(locationSolution, tree) is string project
				? root.Child(project)
				: root;
			SymbolNode node = fileParent
				.ChildPath(SolutionRelativePath.Of(solutionDirectory, span.Path)!)
				.Child(enclosing.Namespace);
			foreach (EnclosingSegment segment in enclosing.Segments)
				node = node.Child($"{segment.Kind},{OutlineBuilder.Field(segment.Name)}");

			node.AddLocation(
				span.StartLinePosition.Line + 1,
				span.StartLinePosition.Character + 1,
				span.EndLinePosition.Line + 1,
				span.EndLinePosition.Character + 1);
		}

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", SymbolResolver.FullyQualifiedName(representative));
		if (truncated)
		{
			builder.Header("count", hits.Count);
			builder.Header("truncated", true);
		}
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
