using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.GetMembers;

[McpServerToolType]
public sealed class GetMembersTool
{
	public const string GetMembersName = "get_members";

	private const string MetadataBucket = "<metadata>";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public GetMembersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Lists methods/properties/fields/events/nested types grouped by declaring file ('<metadata>' for
		referenced assemblies). Razor-generated members are placed under their .razor/.cshtml file with
		the mapped location (@inject directive lines included). Leaves: <memberKind>,<name>,<loc>, plus
		pipe-delimited parameter types for methods with parameters. Metadata loc is empty. Local functions
		nest under their member in declaration order, one level per nesting. Property/event accessors are
		not listed as methods: each property/event appears once, as its own leaf, and includeMethods covers
		ordinary methods, constructors and local functions only.

		Includes all accessibilities. Filter with nameFilter/include*; use get_symbol_body for bodies.
		symbolName accepts a uniquely-matching dotted-segment suffix.
		""")]
	[McpServerTool(
		Name = GetMembersName,
		Title = "Get a type's members",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetMembers(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.SymbolName)] string symbolName,
		[Description("Include inherited members.")] bool includeInherited = false,
		[Description("Case-insensitive name filter: trailing '*' means prefix; otherwise substring. Null matches all.")] string? nameFilter = null,
		[Description("Include methods (ordinary methods and constructors; property/event accessors are represented by their property/event leaf).")] bool includeMethods = true,
		[Description("Include fields.")] bool includeFields = true,
		[Description("Include properties.")] bool includeProperties = true,
		[Description("Include events.")] bool includeEvents = true,
		[Description("Include nested types.")] bool includeNestedTypes = true,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetMembersCoreAsync(model, instance, symbolName, includeInherited, nameFilter, includeMethods, includeFields, includeProperties, includeEvents, includeNestedTypes, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> GetMembersCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		bool includeInherited = false,
		string? nameFilter = null,
		bool includeMethods = true,
		bool includeFields = true,
		bool includeProperties = true,
		bool includeEvents = true,
		bool includeNestedTypes = true,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing(instance.LoadedProjects));

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve the type in every projection so members declared in a branch inactive in the loaded
		// configuration are included; the strictest matching level any projection reached decides for all
		// of them, and grouping by fully-qualified name keeps the same type across projections one. The
		// copy kept as the representative is the best bound, so its parameter types are the ones echoed.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(
			SymbolResolver,
			projections,
			symbolName,
			includeMetadata: true,
			accept: symbol => symbol is INamedTypeSymbol);

		if (groups.Count == 0)
			return Failure(Error.NotFound($"No type matched '{symbolName}'."));
		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(symbolName, groups.Select(group => group[0].Symbol)));

		// Holding the symbols, so an ambiguous match can emit candidates that are distinguishable from each
		// other and accepted back verbatim.
		var typeInstances = groups[0]
			.Select(projectionSymbol => (Type: (INamedTypeSymbol)projectionSymbol.Symbol, projectionSymbol.Projection.Solution))
			.ToList();

		// Of the copies of the same type across projections, the best-bound one is the representative
		// whose signature is echoed.
		var typeCopies = new DeclarationCopies<Solution>();
		foreach ((INamedTypeSymbol typeInstance, Solution typeSolution) in typeInstances)
			typeCopies.Add(typeInstance, typeSolution);
		INamedTypeSymbol type = (INamedTypeSymbol)typeCopies.Items.Single().Symbol;

		bool NameMatches(string name)
		{
			if (string.IsNullOrEmpty(nameFilter))
				return true;
			if (nameFilter.EndsWith('*'))
				return name.StartsWith(nameFilter[..^1], StringComparison.OrdinalIgnoreCase);

			return name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase);
		}

		bool KindIncluded(ISymbol member) =>
			member.Kind switch
			{
				SymbolKind.Method => includeMethods,
				SymbolKind.Field => includeFields,
				SymbolKind.Property => includeProperties,
				SymbolKind.Event => includeEvents,
				SymbolKind.NamedType => includeNestedTypes,
				_ => true,
			};

		// Union members across every projection instance of the type, deduped by stable identity so a member
		// in shared code is listed once while a member declared only in an inactive branch still appears;
		// the copy kept is the one whose signature binds best, so its parameter types are the ones echoed.
		var memberCopies = new DeclarationCopies<Solution>();
		foreach ((INamedTypeSymbol typeInstance, Solution typeSolution) in typeInstances)
		{
			foreach (ISymbol member in Collect(typeInstance, includeInherited)
				.Where(member => !member.IsImplicitlyDeclared)
				// Property and event accessors are methods in Roslyn, but they are represented once by the
				// property/event leaf, so includeMethods covers ordinary methods, constructors and local
				// functions only. AssociatedSymbol also covers explicit interface implementations and
				// inherited accessors.
				.Where(member => member is not IMethodSymbol { AssociatedSymbol: not null })
				.Where(KindIncluded)
				.Where(member => NameMatches(member.Name)))
			{
				memberCopies.Add(member, typeSolution);
			}
		}

		var entries = new List<(string? Project, string File, int Order, string Line, IReadOnlyList<(int Depth, string Line)> LocalFunctions)>();
		foreach ((ISymbol member, Solution memberSolution) in memberCopies.Items)
		{
			(string? project, string file, int order, string line) = await RenderAsync(member, memberSolution, solutionDirectory, token);
			var localFunctions = new List<(int Depth, string Line)>();
			await AddLocalFunctionsAsync(localFunctions, member, depth: 1, memberSolution, solutionDirectory, token);
			entries.Add((project, file, order, line, localFunctions));
		}

		var byProject = entries
			.GroupBy(entry => entry.Project)
			.OrderBy(group => group.Key is null)
			.ThenBy(group => group.Key, StringComparer.Ordinal);

		void RenderBody(OutlineBuilder target, List<OutlineItem>? collect)
		{
			foreach (var project in byProject)
			{
				int fileDepth = 0;
				if (project.Key is string projectName)
				{
					target.Line(0, projectName);
					fileDepth = 1;
				}

				FolderFiles.Write(target, fileDepth, project, entry => entry.File, (memberDepth, file) =>
				{
					foreach (var entry in file.OrderBy(item => item.Order).ThenBy(item => item.Line, StringComparer.Ordinal))
					{
						target.Line(memberDepth, entry.Line);
						foreach ((int depth, string localLine) in entry.LocalFunctions)
							target.Line(memberDepth + depth, localLine);

						if (collect is not null)
						{
							// The chain is the project line, folder line and file line the member renders
							// under; its local functions ride with it as deeper lines of the same item.
							(string? folder, string fileName) = OutlinePath.Split(entry.File);
							string[] context;
							if (project.Key is string p)
								context = folder is null ? [p, fileName] : [p, folder, fileName];
							else
								context = folder is null ? [fileName] : [folder, fileName];
							var lines = new List<(int Depth, string Text)> { (0, entry.Line) };
							foreach ((int depth, string localLine) in entry.LocalFunctions)
								lines.Add((depth, localLine));
							collect.Add(new OutlineItem(lines.ToArray(), context));
						}
					}
				});
			}
		}

		var collected = new List<OutlineItem>();
		RenderBody(new OutlineBuilder(), collected);

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => builder.Header("resolvedType", SymbolResolver.SignatureName(type)),
			builder => RenderBody(builder, null),
			collected,
			startItem,
			maxSize,
			budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}

	/// <summary>
	/// Appends the local functions declared in <paramref name="container"/>, each followed by its own, in
	/// declaration order, one level deeper per nesting.
	/// </summary>
	private static async Task AddLocalFunctionsAsync(
		List<(int Depth, string Line)> lines,
		ISymbol container,
		int depth,
		Solution solution,
		string? solutionDirectory,
		CancellationToken cancellationToken)
	{
		if (container is not (IMethodSymbol or IPropertySymbol or IEventSymbol) || container.DeclaringSyntaxReferences.IsEmpty)
			return;

		IEnumerable<IMethodSymbol> declared = (await LocalFunctions.FindAllInAsync(solution, container, cancellationToken))
			.OrderBy(local => local.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0);
		foreach (IMethodSymbol local in declared)
		{
			lines.Add((depth, (await RenderAsync(local, solution, solutionDirectory, cancellationToken)).Line));
			await AddLocalFunctionsAsync(lines, local, depth + 1, solution, solutionDirectory, cancellationToken);
		}
	}

	private static async Task<(string? Project, string File, int Order, string Line)> RenderAsync(
		ISymbol member,
		Solution solution,
		string? solutionDirectory,
		CancellationToken cancellationToken)
	{
		SyntaxReference? reference = member.DeclaringSyntaxReferences.FirstOrDefault();
		if (reference is null)
			return (null, MetadataBucket, 0, Line(member, ""));

		SyntaxTree tree = reference.SyntaxTree;
		FileLinePositionSpan span = tree.GetDisplaySpan(reference.Span);

		// A Razor-generated declaration that crosses #line regions (@inject properties, BuildRenderTree)
		// has no single mapped span, so it is placed under the .razor/.cshtml file its first mapped
		// region names, with that region's location.
		if (!span.HasMappedPath && RazorMapping.IsRazorGeneratedDocument(tree))
		{
			SourceText generated = await reference.SyntaxTree.GetTextAsync(cancellationToken);
			IReadOnlyList<RazorSourceRange> mapped = await RazorMapping.MapToRazorSourceAsync(
				solution, reference.SyntaxTree, reference.Span, generated, cancellationToken);
			if (mapped.Count > 0)
			{
				RazorSourceRange first = mapped[0];
				return (
					ProjectName.Of(first.Project),
					SolutionRelativePath.Of(solutionDirectory, first.Path)!,
					first.Span.StartLinePosition.Line + 1,
					Line(member, Location(first.Span)));
			}
		}

		string file = GeneratedSource.MarkPath(SolutionRelativePath.Of(solutionDirectory, span.Path)!, solution, tree);
		string? project = ProjectName.Of(solution, tree);
		int order = span.StartLinePosition.Line + 1;

		return (project, file, order, Line(member, Location(span)));
	}

	private static string Location(FileLinePositionSpan span) =>
		$"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}";

	private static string Line(ISymbol member, string location)
	{
		string line = $"{SymbolKindText.Of(member)},{OutlineBuilder.Field(member.Name)},{location}";

		if (member is IMethodSymbol { Parameters.Length: > 0 } method)
			line += "," + Signature(method);

		return line;
	}

	private static string Signature(IMethodSymbol method) => ParameterTypes.Of(method);

	private static IEnumerable<ISymbol> Collect(INamedTypeSymbol type, bool includeInherited)
	{
		for (INamedTypeSymbol? current = type; current is not null; current = includeInherited ? current.BaseType : null)
		{
			foreach (ISymbol member in current.GetMembers())
				yield return member;
		}
	}
}
