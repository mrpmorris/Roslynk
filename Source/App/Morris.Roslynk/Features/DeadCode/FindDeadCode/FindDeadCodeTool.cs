using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.References;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.DeadCode.FindDeadCode;

[McpServerToolType]
public sealed class FindDeadCodeTool
{
	public const string FindDeadCodeName = "find_dead_code";

	private const string NoLocationBucket = "<no-location>";

	private static readonly HashSet<string> TestMethodAttributes = new(StringComparer.Ordinal)
	{
		"FactAttribute",
		"TheoryAttribute",
		"TestAttribute",
		"TestMethodAttribute",
		"TestCaseAttribute",
	};

	private static readonly HashSet<string> TestTypeAttributes = new(StringComparer.Ordinal)
	{
		"TestFixtureAttribute",
		"TestClassAttribute",
	};

	private static readonly string[] GeneratedFileSuffixes =
	[
		".g.cs",
		".g.i.cs",
		".designer.cs",
		".generated.cs",
	];

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ResponseBudget Budget;

	public FindDeadCodeTool(InstanceRegistry instanceRegistry, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Finds unreferenced types/methods/properties/fields/events, conservatively excluding interface
		implementations, virtual/override chains, tests, generated and DI/reflection-activated members.
		Excludes public APIs unless includePublic. find_dead_conditionals finds unreachable #if branches.

		Body: project > folder > file > namespace > type; leaves:
		<memberKind>,<memberName>,<loc>,<confidence>,<reason>, confidence=High|Medium.
		Dead types have their own leaf/location. loc spans the declaration for apply_patch removal.
		Unused overloads have separate locations, without parameter lists: get_symbol_body offers
		candidates. The host decides removals. Scan is per-symbol; narrow large solutions with scope.

		maxSize caps the scan: count is candidates found; truncated=Y may mean the cap stopped scanning.
		Omit maxSize for a complete scan.

		countOnly=true returns the headers plus count=<n> and no leaves; maxSize still caps the scan,
		so truncated=Y means the cap stopped the scan.
		""")]
	[McpServerTool(
		Name = FindDeadCodeName,
		Title = "Find unused code",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindDeadCode(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Name prefix, e.g. MyApp.Services; omit for the whole solution.")] string? scope = null,
		[Description("Include unreferenced public/protected members (the API surface).")] bool includePublic = false,
		[Description("Scan cap; omit for a complete scan.")] int maxSize = 0,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description("Return counts only: count= and no leaves, no body and no paging headers; startItem is ignored, while maxSize still caps the scan.")] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync();
		return await FindDeadCodeCoreAsync(model, instance, scope, includePublic, maxSize, startItem, countOnly, Budget, CancellationToken.None);
	}

	internal async Task<string> FindDeadCodeCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string? scope = null,
		bool includePublic = false,
		int maxSize = 0,
		int startItem = 1,
		bool countOnly = false,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status);

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		HashSet<ProjectId> testProjects = IdentifyTestProjects(solution);

		// A declaration compiled into several projects is one finding, keyed like the other tools; the copy
		// kept is the best bound, so the reported name comes from where the signature binds.
		var found = new DeclarationCopies<Solution>();
		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync();
			if (compilation is null)
				continue;

			IMethodSymbol? entryPoint = compilation.GetEntryPoint(CancellationToken.None);
			foreach (ISymbol symbol in EnumerateDeclarations(compilation.GlobalNamespace))
			{
				if (!IsCandidate(symbol, entryPoint, includePublic))
					continue;

				// scope is a namespace/type prefix, so it is matched against the parameterless name; the
				// dedupe key carries the signature so two unused overloads are two findings, not one.
				if (scope is not null && !SymbolResolver.FullyQualifiedName(symbol).StartsWith(scope, StringComparison.OrdinalIgnoreCase))
					continue;

				found.Add(symbol, solution);
			}
		}

		List<ISymbol> candidates = found.Items.Select(item => item.Symbol).ToList();
		candidates.Sort((left, right) => string.Compare(
			SymbolResolver.SignatureName(left), SymbolResolver.SignatureName(right), StringComparison.Ordinal));

		int cap = maxSize > 0 ? maxSize : int.MaxValue;
		var results = new List<Candidate>();
		bool truncated = false;
		foreach (ISymbol symbol in candidates)
		{
			Candidate? candidate = await EvaluateAsync(solution, symbol, testProjects, solutionDirectory, token);
			if (candidate is null)
				continue;

			results.Add(candidate);
			if (results.Count >= cap)
			{
				truncated = true;
				break;
			}
		}

		bool capped = truncated;

		var root = new SymbolNode();
		foreach (Candidate candidate in results)
		{
			SymbolNode start = candidate.Project is string project ? root.Child(project) : root;
			SymbolNode node = start.ChildPath(candidate.File).Child(candidate.Namespace);
			foreach (string type in candidate.ContainingTypes)
				node = node.Child(type);

			node.Child(candidate.Leaf);
		}

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => { },
			capHeaders: capped
				? builder =>
				{
					// countOnly: ItemPaging writes the single count= for the items; only the cap's signal is ours.
					if (!countOnly)
						builder.Header("count", results.Count);
					builder.Header("truncated", true);
				}
				: null,
			renderBody: builder => root.Render(builder),
			items: root.Items(),
			startItem: startItem,
			maxSize: maxSize,
			budget: budget,
			countOnly: countOnly);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => OutlineError.Format(Error.Invalid(invalid.Message), model.Status),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}

	private static async Task<Candidate?> EvaluateAsync(Solution solution, ISymbol symbol, HashSet<ProjectId> testProjects, string? solutionDirectory, CancellationToken token)
	{
		int nonTestReferences = 0;
		int testReferences = 0;
		bool dispose = SpuriousDisposeReferences.IsDisposeMethod(symbol);
		foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution))
		{
			foreach (ReferenceLocation location in referenced.Locations)
			{
				// Roslyn 5.9 reports a plain disposable local declaration as a Dispose reference; it must
				// not keep a genuinely unused Dispose alive.
				if (dispose && await SpuriousDisposeReferences.IsSpuriousAsync(location.Location, token))
					continue;

				if (testProjects.Contains(location.Document.Project.Id))
					testReferences++;
				else
					nonTestReferences++;
			}
		}

		if (nonTestReferences > 0)
			return null;

		(string reason, string confidence) = testReferences > 0
			? ("Only referenced by test code", "Medium")
			: IsExternallyVisible(symbol)
				? ("No references found (public API; may be used externally)", "Medium")
				: ("No references found", "High");

		SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
		FileLinePositionSpan? span = reference?.SyntaxTree.GetDisplaySpan(reference.Span);

		string file = span is { } located
			? SolutionRelativePath.Of(solutionDirectory, located.Path)!
			: NoLocationBucket;
		string? project = reference is null ? null : ProjectName.Of(solution, reference.SyntaxTree);
		string @namespace = NamespaceOf(symbol);
		IReadOnlyList<string> containingTypes = ContainingTypesOf(symbol);

		string head = $"{SymbolKindText.Of(symbol)},{OutlineBuilder.Field(symbol.Name)}";
		string leaf = span is { } range
			? $"{head},{FormatRange(range)},{confidence},{reason}"
			: $"{head},{confidence},{reason}";

		return new Candidate(project, file, @namespace, containingTypes, leaf);
	}

	private static string NamespaceOf(ISymbol symbol)
	{
		INamespaceSymbol? containing = symbol.ContainingNamespace;
		return containing is null || containing.IsGlobalNamespace ? SymbolPlacement.GlobalNamespace : containing.ToDisplayString();
	}

	private static IReadOnlyList<string> ContainingTypesOf(ISymbol symbol)
	{
		var types = new List<string>();
		for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
			types.Insert(0, $"{SymbolKindText.Of(type)},{OutlineBuilder.Field(type.Name)}");

		return types;
	}

	private static string FormatRange(FileLinePositionSpan span) =>
		$"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}";

	private sealed class Candidate
	{
		public string? Project { get; }
		public string File { get; }
		public string Namespace { get; }
		public IReadOnlyList<string> ContainingTypes { get; }
		public string Leaf { get; }

		public Candidate(string? project, string file, string @namespace, IReadOnlyList<string> containingTypes, string leaf)
		{
			Project = project;
			File = file;
			Namespace = @namespace;
			ContainingTypes = containingTypes;
			Leaf = leaf;
		}
	}

	private static bool IsCandidate(ISymbol symbol, IMethodSymbol? entryPoint, bool includePublic)
	{
		if (symbol.IsImplicitlyDeclared || !symbol.Locations.Any(location => location.IsInSource))
			return false;
		if (IsInGeneratedFile(symbol) || HasGeneratedCodeAttribute(symbol) || IsTest(symbol) || HasReflectionAttribute(symbol))
			return false;

		switch (symbol)
		{
			case INamedTypeSymbol type when type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate:
				break;
			case IMethodSymbol method when method.MethodKind == MethodKind.Ordinary && !SymbolEqualityComparer.Default.Equals(method, entryPoint):
				break;
			case IPropertySymbol:
			case IEventSymbol:
				break;
			case IFieldSymbol field when field.ContainingType.TypeKind != TypeKind.Enum:
				break;
			default:
				return false;
		}

		if (!includePublic && IsExternallyVisible(symbol))
			return false;
		if (IsOverridable(symbol) || ImplementsInterfaceMember(symbol))
			return false;

		return true;
	}

	private static IEnumerable<ISymbol> EnumerateDeclarations(INamespaceSymbol @namespace)
	{
		foreach (INamedTypeSymbol type in @namespace.GetTypeMembers())
		{
			foreach (ISymbol symbol in EnumerateType(type))
				yield return symbol;
		}

		foreach (INamespaceSymbol child in @namespace.GetNamespaceMembers())
		{
			foreach (ISymbol symbol in EnumerateDeclarations(child))
				yield return symbol;
		}
	}

	private static IEnumerable<ISymbol> EnumerateType(INamedTypeSymbol type)
	{
		yield return type;
		foreach (ISymbol member in type.GetMembers())
		{
			if (member is INamedTypeSymbol nested)
			{
				foreach (ISymbol symbol in EnumerateType(nested))
					yield return symbol;
			}
			else
			{
				yield return member;
			}
		}
	}

	private static HashSet<ProjectId> IdentifyTestProjects(Solution solution)
	{
		var testProjects = new HashSet<ProjectId>();
		foreach (Project project in solution.Projects)
		{
			bool referencesTestFramework = project.MetadataReferences.Any(reference =>
				reference.Display is string display
				&& (display.Contains("xunit", StringComparison.OrdinalIgnoreCase)
					|| display.Contains("nunit", StringComparison.OrdinalIgnoreCase)
					|| display.Contains("mstest", StringComparison.OrdinalIgnoreCase)
					|| display.Contains("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase)));

			if (referencesTestFramework)
				testProjects.Add(project.Id);
		}

		return testProjects;
	}

	private static bool IsExternallyVisible(ISymbol symbol) =>
		symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

	private static bool IsOverridable(ISymbol symbol) =>
		symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride;

	private static bool ImplementsInterfaceMember(ISymbol symbol)
	{
		if (symbol is not (IMethodSymbol or IPropertySymbol or IEventSymbol))
			return false;

		bool explicitImplementation = symbol switch
		{
			IMethodSymbol method => method.ExplicitInterfaceImplementations.Length > 0,
			IPropertySymbol property => property.ExplicitInterfaceImplementations.Length > 0,
			IEventSymbol @event => @event.ExplicitInterfaceImplementations.Length > 0,
			_ => false,
		};
		if (explicitImplementation)
			return true;

		INamedTypeSymbol containingType = symbol.ContainingType;
		foreach (INamedTypeSymbol @interface in containingType.AllInterfaces)
		{
			foreach (ISymbol member in @interface.GetMembers())
			{
				if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(member), symbol))
					return true;
			}
		}

		return false;
	}

	private static bool IsTest(ISymbol symbol)
	{
		if (HasAttribute(symbol, TestMethodAttributes.Contains))
			return true;

		INamedTypeSymbol? type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
		if (type is null)
			return false;

		return HasAttribute(type, TestTypeAttributes.Contains)
			|| type.GetMembers().OfType<IMethodSymbol>().Any(method => HasAttribute(method, TestMethodAttributes.Contains));
	}

	private static bool HasGeneratedCodeAttribute(ISymbol symbol) =>
		HasAttribute(symbol, IsGeneratedAttributeName)
		|| (symbol.ContainingType is INamedTypeSymbol type && HasAttribute(type, IsGeneratedAttributeName));

	private static bool IsGeneratedAttributeName(string name) =>
		name is "GeneratedCodeAttribute" or "CompilerGeneratedAttribute";

	private static bool HasReflectionAttribute(ISymbol symbol) =>
		HasAttribute(symbol, name => name is "ExportAttribute" or "ImportingConstructorAttribute");

	private static bool HasAttribute(ISymbol symbol, Func<string, bool> nameMatches) =>
		symbol.GetAttributes().Any(attribute => attribute.AttributeClass is INamedTypeSymbol attributeClass && nameMatches(attributeClass.Name));

	private static bool IsInGeneratedFile(ISymbol symbol) =>
		symbol.Locations.Any(location => location.SourceTree is SyntaxTree tree && IsGeneratedPath(tree.FilePath));

	private static bool IsGeneratedPath(string? path)
	{
		if (string.IsNullOrEmpty(path))
			return false;
		if (GeneratedFileSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
			return true;

		foreach (string segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
		{
			if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}
}
