using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
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

	public GetMembersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
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
		fully-qualified name. {OutlineDescriptions.TextNotJson} Members are grouped by the file that declares
		them (or '<metadata>' for a referenced assembly), each as:
		  #resolvedType=<fully-qualified type>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs>
		  \t\t\t<memberKind>,<name>,<loc>,<paramType|paramType|...>
		where kind is one of {OutlineDescriptions.KindList}, {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}; the loc is empty for a metadata member; the trailing signature is a pipe-delimited list of minimally-qualified parameter types, present only for methods that take parameters. To read a member's
		body, resolve its path against the solution folder and read startLine through endLine. Private and
		inherited members are excluded unless requested; narrow a large type with nameFilter (a trailing '*'
		matches by prefix, otherwise a case-insensitive substring) and the include* kind toggles.
		{OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over reading the .cs file; it is the compiler's view,
		correct across partial classes and (with includeInherited) base types.
		""")]
	public async Task<string> GetMembers(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the type, e.g. 'MyNamespace.MyType'.")] string typeName,
		[Description("Include private members. Default false.")] bool includePrivate = false,
		[Description("Include members inherited from base types. Default false.")] bool includeInherited = false,
		[Description("Optional case-insensitive filter on member name: a trailing '*' matches by prefix (e.g. 'Search*'), otherwise it is a substring match. Default null (no name filtering).")] string? nameFilter = null,
		[Description("Include method members. Default true.")] bool includeMethods = true,
		[Description("Include field members. Default true.")] bool includeFields = true,
		[Description("Include property members. Default true.")] bool includeProperties = true,
		[Description("Include event members. Default true.")] bool includeEvents = true,
		[Description("Include nested type members. Default true.")] bool includeNestedTypes = true)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = await instance.ReadModelAsync();

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve the type in every projection so members declared in a branch inactive in the loaded
		// configuration are included; group by fully-qualified name so the same type across projections is one.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		var typeInstances = new List<(INamedTypeSymbol Type, Solution Solution)>();
		var distinctTypes = new HashSet<string>(StringComparer.Ordinal);
		foreach (Projection projection in projections)
		{
			foreach (INamedTypeSymbol candidate in (await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(projection.Solution, typeName)).OfType<INamedTypeSymbol>())
			{
				typeInstances.Add((candidate, projection.Solution));
				distinctTypes.Add(SymbolResolver.FullyQualifiedName(candidate));
			}
		}

		if (distinctTypes.Count == 0)
			return Failure(Error.NotFound($"No type matched '{typeName}'."));
		if (distinctTypes.Count > 1)
			return Failure(Error.Ambiguous($"'{typeName}' matched multiple types.", distinctTypes.OrderBy(name => name, StringComparer.Ordinal).ToArray()));

		INamedTypeSymbol type = typeInstances[0].Type;

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
		// in shared code is listed once while a member declared only in an inactive branch still appears.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var entries = new List<(string? Project, string File, int Order, string Line)>();
		foreach ((INamedTypeSymbol typeInstance, Solution typeSolution) in typeInstances)
		{
			foreach (ISymbol member in Collect(typeInstance, includeInherited)
				.Where(member => !member.IsImplicitlyDeclared)
				.Where(member => includePrivate || member.DeclaredAccessibility != Accessibility.Private)
				.Where(KindIncluded)
				.Where(member => NameMatches(member.Name)))
			{
				if (seen.Add(ProjectionService.KeyOf(member)))
					entries.Add(Render(member, typeSolution, solutionDirectory));
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("resolvedType", SymbolResolver.FullyQualifiedName(type));
		builder.Status(model.Status);
		builder.BeginBody();

		var byProject = entries
			.GroupBy(entry => entry.Project)
			.OrderBy(group => group.Key is null)
			.ThenBy(group => group.Key, StringComparer.Ordinal);

		foreach (var project in byProject)
		{
			int fileDepth = 0;
			if (project.Key is string projectName)
			{
				builder.Line(0, projectName);
				fileDepth = 1;
			}

			FolderFiles.Write(builder, fileDepth, project, entry => entry.File, (memberDepth, file) =>
			{
				foreach (var entry in file.OrderBy(item => item.Order).ThenBy(item => item.Line, StringComparer.Ordinal))
					builder.Line(memberDepth, entry.Line);
			});
		}

		return builder.ToString();
	}

	private static (string? Project, string File, int Order, string Line) Render(ISymbol member, Solution solution, string? solutionDirectory)
	{
		SyntaxReference? reference = member.DeclaringSyntaxReferences.FirstOrDefault();
		FileLinePositionSpan? span = reference?.SyntaxTree.GetLineSpan(reference.Span);

		string file = span is { } located
			? SolutionRelativePath.Of(solutionDirectory, located.Path)!
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

	private static string Signature(IMethodSymbol method) =>
		string.Join('|', method.Parameters.Select(parameter => OutlineBuilder.Field(parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))));

	private static IEnumerable<ISymbol> Collect(INamedTypeSymbol type, bool includeInherited)
	{
		for (INamedTypeSymbol? current = type; current is not null; current = includeInherited ? current.BaseType : null)
		{
			foreach (ISymbol member in current.GetMembers())
				yield return member;
		}
	}
}
