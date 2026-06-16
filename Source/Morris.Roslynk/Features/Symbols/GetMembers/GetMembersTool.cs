using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
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

	public GetMembersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
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
		  #count=<member count>
		  #status=Ready

		  <relative/forward-slash/path.cs>
		  \t<memberKind>,<name>,<startLine>-<endLine> <signature>
		where kind is one of {OutlineDescriptions.KindList}; the line range collapses to a single number when
		start equals end and is omitted for metadata members; the trailing signature is the minimally-qualified
		parameter list for methods (e.g. '(CancellationToken)') and absent for other kinds. To read a member's
		body, resolve its path against the solution folder and read startLine through endLine. Private and
		inherited members are excluded unless requested; narrow a large type with nameFilter (a trailing '*'
		matches by prefix, otherwise a case-insensitive substring) and the include* kind toggles.
		{OutlineDescriptions.ErrorBlock} Prefer this over reading the .cs file; it is the compiler's view,
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
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		List<INamedTypeSymbol> types = (await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, typeName))
			.OfType<INamedTypeSymbol>()
			.ToList();

		if (types.Count == 0)
			return Failure(Error.NotFound($"No type matched '{typeName}'."));
		if (types.Count > 1)
		{
			string[] candidates = types.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{typeName}' matched multiple types.", candidates));
		}

		INamedTypeSymbol type = types[0];

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

		List<ISymbol> members = Collect(type, includeInherited)
			.Where(member => !member.IsImplicitlyDeclared)
			.Where(member => includePrivate || member.DeclaredAccessibility != Accessibility.Private)
			.Where(KindIncluded)
			.Where(member => NameMatches(member.Name))
			.ToList();

		var byFile = new SortedDictionary<string, List<(int Order, string Line)>>(StringComparer.Ordinal);
		foreach (ISymbol member in members)
		{
			(string file, int order, string line) = Render(member, solutionDirectory);
			if (!byFile.TryGetValue(file, out List<(int, string)>? lines))
				byFile[file] = lines = [];

			lines.Add((order, line));
		}

		var builder = new OutlineBuilder();
		builder.Header("resolvedType", SymbolResolver.FullyQualifiedName(type));
		builder.Header("count", members.Count);
		builder.Status(model.Status);
		builder.BeginBody();

		foreach (KeyValuePair<string, List<(int Order, string Line)>> file in byFile)
		{
			builder.Line(0, file.Key);
			foreach ((int _, string line) in file.Value.OrderBy(entry => entry.Order).ThenBy(entry => entry.Line, StringComparer.Ordinal))
				builder.Line(1, line);
		}

		return builder.ToString();
	}

	private static (string File, int Order, string Line) Render(ISymbol member, string? solutionDirectory)
	{
		SyntaxReference? reference = member.DeclaringSyntaxReferences.FirstOrDefault();
		FileLinePositionSpan? span = reference?.SyntaxTree.GetLineSpan(reference.Span);

		string file = span is { } located
			? SolutionRelativePath.Of(solutionDirectory, located.Path)!
			: MetadataBucket;
		int startLine = span is { } start ? start.StartLinePosition.Line + 1 : 0;
		int endLine = span is { } end ? end.EndLinePosition.Line + 1 : 0;

		string line = $"{SymbolKindText.Of(member)},{member.Name}";
		if (span is not null)
			line += "," + (startLine == endLine ? startLine.ToString() : $"{startLine}-{endLine}");

		if (member is IMethodSymbol method)
			line += " " + Signature(method);

		return (file, startLine, line);
	}

	private static string Signature(IMethodSymbol method) =>
		"(" + string.Join(", ", method.Parameters.Select(parameter => parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))) + ")";

	private static IEnumerable<ISymbol> Collect(INamedTypeSymbol type, bool includeInherited)
	{
		for (INamedTypeSymbol? current = type; current is not null; current = includeInherited ? current.BaseType : null)
		{
			foreach (ISymbol member in current.GetMembers())
				yield return member;
		}
	}
}
