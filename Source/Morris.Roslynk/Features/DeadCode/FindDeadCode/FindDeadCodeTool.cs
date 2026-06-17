using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
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

	public FindDeadCodeTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = FindDeadCodeName,
		Title = "Find unused code",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Finds symbols (types, methods, properties, fields, events) with no references, conservatively filtered
		to avoid false positives: it excludes interface implementations, virtual/override chains, test members,
		generated code, and DI/reflection-activated members, and (unless includePublic is true) the public API
		surface. {OutlineDescriptions.TextNotJson} Candidates nest file -> namespace -> type -> member:

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>,<confidence> <reason>
		where kind is one of {OutlineDescriptions.KindList}, {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}; confidence is High or
		Medium, and the free-text reason is last; a dead type is itself a leaf carrying its own loc. The loc is
		the full declaration span, ready to pass to apply_patch to remove the member. The host decides whether to remove a candidate. Scan is per-symbol, so narrow large
		solutions with scope. {OutlineDescriptions.TruncationFlag} {OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock}
		""")]
	public async Task<string> FindDeadCode(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Optional fully-qualified-name prefix to limit the scan, e.g. MyApp.Services. Omit for the whole solution.")] string? scope = null,
		[Description("Include unreferenced public/protected members (the API surface). Default false.")] bool includePublic = false,
		[Description("Maximum candidates to return. Default 50.")] int maxResults = 50)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		HashSet<ProjectId> testProjects = IdentifyTestProjects(solution);

		var candidates = new List<ISymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
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

				string fullyQualified = SymbolResolver.FullyQualifiedName(symbol);
				if (scope is not null && !fullyQualified.StartsWith(scope, StringComparison.OrdinalIgnoreCase))
					continue;
				if (!seen.Add(fullyQualified))
					continue;

				candidates.Add(symbol);
			}
		}

		candidates.Sort((left, right) => string.Compare(
			SymbolResolver.FullyQualifiedName(left), SymbolResolver.FullyQualifiedName(right), StringComparison.Ordinal));

		var results = new List<Candidate>();
		bool truncated = false;
		foreach (ISymbol symbol in candidates)
		{
			Candidate? candidate = await EvaluateAsync(solution, symbol, testProjects, solutionDirectory);
			if (candidate is null)
				continue;

			results.Add(candidate);
			if (results.Count >= maxResults)
			{
				truncated = true;
				break;
			}
		}

		var builder = new OutlineBuilder();
		if (truncated)
			builder.Header("truncated", true);
		builder.Status(model.Status);
		builder.BeginBody();

		var root = new SymbolNode();
		foreach (Candidate candidate in results)
		{
			SymbolNode start = candidate.Project is string project ? root.Child(project) : root;
			SymbolNode node = start.ChildPath(candidate.File).Child(candidate.Namespace);
			foreach (string type in candidate.ContainingTypes)
				node = node.Child(type);

			node.Child(candidate.Leaf);
		}

		root.Render(builder);
		return builder.ToString();
	}

	private static async Task<Candidate?> EvaluateAsync(Solution solution, ISymbol symbol, HashSet<ProjectId> testProjects, string? solutionDirectory)
	{
		int nonTestReferences = 0;
		int testReferences = 0;
		foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution))
		{
			foreach (ReferenceLocation location in referenced.Locations)
			{
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
		FileLinePositionSpan? span = reference?.SyntaxTree.GetLineSpan(reference.Span);

		string file = span is { } located
			? SolutionRelativePath.Of(solutionDirectory, located.Path)!
			: NoLocationBucket;
		string? project = reference is null ? null : ProjectName.Of(solution, reference.SyntaxTree);
		string @namespace = NamespaceOf(symbol);
		IReadOnlyList<string> containingTypes = ContainingTypesOf(symbol);

		string head = $"{SymbolKindText.Of(symbol)},{OutlineBuilder.Field(symbol.Name)}";
		string leaf = span is { } range
			? $"{head},{FormatRange(range)},{confidence} {reason}"
			: $"{head},{confidence} {reason}";

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
