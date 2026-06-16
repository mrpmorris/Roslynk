using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
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

	public FindReferencesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
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
		  \t<relative/forward-slash/path.cs>
		  \t\t<namespace, or "<global>">
		  \t\t\t<typeKind>,<typeName>,<loc|loc|...>   (locations present only when the type declaration itself references the symbol)
		  \t\t\t\t<memberKind>,<memberName>,<loc|loc|...>
		where kind is one of {OutlineDescriptions.KindList}; {OutlineDescriptions.Loc}; {OutlineDescriptions.LocList}; {OutlineDescriptions.ListFieldQuoting}.
		{OutlineDescriptions.Truncation} {OutlineDescriptions.Project} {OutlineDescriptions.ErrorBlock} Prefer this over grepping: it matches the compiler's symbol, not text,
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
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(solution, symbolName);

		if (matches.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(match => match.ToDisplayString()).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched several symbols.", candidates));
		}

		ISymbol symbol = matches[0];

		var locations = new List<Location>();
		foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution))
		{
			foreach (ReferenceLocation reference in referenced.Locations)
			{
				if (reference.Location.IsInSource)
					locations.Add(reference.Location);
			}
		}

		List<Location> page = locations.Take(Math.Max(0, maxResults)).ToList();
		bool truncated = locations.Count > page.Count;

		var root = new SymbolNode();
		foreach (Location location in page)
		{
			EnclosingPath enclosing = await EnclosingDeclaration.ResolveAsync(solution, location, cancellationToken);
			FileLinePositionSpan span = location.GetLineSpan();

			SymbolNode fileParent = location.SourceTree is SyntaxTree tree && ProjectName.Of(solution, tree) is string project
				? root.Child(project)
				: root;
			SymbolNode node = fileParent
				.Child(SolutionRelativePath.Of(solutionDirectory, span.Path)!)
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
		builder.Header("resolvedSymbol", SymbolResolver.FullyQualifiedName(symbol));
		if (truncated)
		{
			builder.Header("count", locations.Count);
			builder.Header("truncated", true);
		}
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
