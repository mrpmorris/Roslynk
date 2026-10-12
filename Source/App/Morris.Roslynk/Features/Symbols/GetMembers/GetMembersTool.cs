using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
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

	[McpServerTool(
		Name = GetMembersName,
		Title = "Get a type's members",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Lists a type's members (methods, properties, fields, events, nested types), resolved by
		fully-qualified name.
		{OutlineDescriptions.ProjectionCoverage}
		{OutlineDescriptions.CommonMethodInstructions}
		Members are grouped by the file that declares them (or '<metadata>' for a referenced assembly), each as:
		  resolvedType=<fully-qualified type>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<memberKind>,<name>,<loc>,<paramType|paramType|...>
		  \t\t\t\tlocalfunction,<name>,<loc>,<paramType|...>
		A member's local functions are listed beneath it in declaration order, each one level deeper, with a local
		function declared inside another nested one level further; name a local function as a member of the
		member declaring it, e.g. 'MyNamespace.MyType.MyMethod.local'.
		where kind is one of {OutlineDescriptions.KindList}, {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}; the loc is empty for a metadata member; the trailing signature is a pipe-delimited list of minimally-qualified parameter types, present only for methods that take parameters. To read a member's
		body, resolve its path against the solution folder and read startLine through endLine. All
		accessibilities including private are listed; inherited members are excluded unless requested; narrow
		a large type with nameFilter (a trailing '*' matches by prefix, otherwise a case-insensitive
		substring) and the include* kind toggles.
		{OutlineDescriptions.Paging} {OutlineDescriptions.Project} {OutlineDescriptions.GeneratedLocations} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over reading the .cs or .razor file; it is the compiler's view,
		correct across partial classes and (with includeInherited) base types.
		""")]
	public async Task<string> GetMembers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description($"Fully-qualified name of the type, e.g. 'MyNamespace.MyType'. {OutlineDescriptions.SymbolNameGrammar}")] string typeName,
		[Description("Include members inherited from base types.")] bool includeInherited = false,
		[Description("Optional case-insensitive filter on member name: a trailing '*' matches by prefix (e.g. 'Search*'), otherwise it is a substring match; null applies no name filtering.")] string? nameFilter = null,
		[Description("Include method members.")] bool includeMethods = true,
		[Description("Include field members.")] bool includeFields = true,
		[Description("Include property members.")] bool includeProperties = true,
		[Description("Include event members.")] bool includeEvents = true,
		[Description("Include nested type members.")] bool includeNestedTypes = true,
		[Description("1-based first item of the page; only pass it to continue from nextStartItem=. Default 1.")] int startItem = 1,
		[Description("Page size in characters; an oversized result is returned as a page with count=/truncated=Y/nextStartItem=. Default: the response budget.")] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetMembersCoreAsync(model, instance, typeName, includeInherited, nameFilter, includeMethods, includeFields, includeProperties, includeEvents, includeNestedTypes, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> GetMembersCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string typeName,
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
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve the type in every projection so members declared in a branch inactive in the loaded
		// configuration are included; the strictest matching level any projection reached decides for all
		// of them, and grouping by fully-qualified name keeps the same type across projections one. The
		// copy kept as the representative is the best bound, so its parameter types are the ones echoed.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(
			SymbolResolver,
			projections,
			typeName,
			includeMetadata: true,
			accept: symbol => symbol is INamedTypeSymbol);

		if (groups.Count == 0)
			return Failure(Error.NotFound($"No type matched '{typeName}'."));
		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(typeName, groups.Select(group => group[0].Symbol)));

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
				.Where(KindIncluded)
				.Where(member => NameMatches(member.Name)))
			{
				memberCopies.Add(member, typeSolution);
			}
		}

		var entries = new List<(string? Project, string File, int Order, string Line, IReadOnlyList<(int Depth, string Line)> LocalFunctions)>();
		foreach ((ISymbol member, Solution memberSolution) in memberCopies.Items)
		{
			(string? project, string file, int order, string line) = Render(member, memberSolution, solutionDirectory);
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
			lines.Add((depth, Render(local, solution, solutionDirectory).Line));
			await AddLocalFunctionsAsync(lines, local, depth + 1, solution, solutionDirectory, cancellationToken);
		}
	}

	private static (string? Project, string File, int Order, string Line) Render(ISymbol member, Solution solution, string? solutionDirectory)
	{
		SyntaxReference? reference = member.DeclaringSyntaxReferences.FirstOrDefault();
		FileLinePositionSpan? span = reference?.SyntaxTree.GetDisplaySpan(reference.Span);

		string file = span is { } located
			? GeneratedSource.MarkPath(SolutionRelativePath.Of(solutionDirectory, located.Path)!, solution, reference!.SyntaxTree)
			: MetadataBucket;
		string? project = reference is null ? null : ProjectName.Of(solution, reference.SyntaxTree);
		int order = span is { } start ? start.StartLinePosition.Line + 1 : 0;

		string location = span is { } range
			? $"{range.StartLinePosition.Line + 1}:{range.StartLinePosition.Character + 1}-{range.EndLinePosition.Line + 1}:{range.EndLinePosition.Character + 1}"
			: "";

		string line = $"{SymbolKindText.Of(member)},{OutlineBuilder.Field(member.Name)},{location}";

		if (member is IMethodSymbol { Parameters.Length: > 0 } method)
			line += "," + Signature(method);

		return (project, file, order, line);
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
