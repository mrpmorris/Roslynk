using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
	public const string GetDiagnosticsName = "get_diagnostics";

	private const string NoLocationBucket = "<no-location>";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly DiagnosticsService DiagnosticsService;

	public GetDiagnosticsTool(InstanceRegistry instanceRegistry, DiagnosticsService diagnosticsService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		DiagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
	}

	[McpServerTool(
		Name = GetDiagnosticsName,
		Title = "Get compiler diagnostics",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Returns diagnostics for an opened solution.
		Costly: it compiles and analyzes every project the edits since the last call affect, which takes seconds on a
		large solution. Make all the edits a task needs first and call this once at the end, not after every edit; a
		repeat call with nothing changed in between is near-instant.
		{OutlineDescriptions.CommonMethodInstructions}
		Per-severity counts are always in the header so filtering is never silent; diagnostics nest file -> severity:
		  errors=<n>
		  warnings=<n>
		  infos=<n>
		  hidden=<n>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<severity>
		  \t\t\t\t<id>,<line:col>,<message>
		where severity is the plural group errors|warnings|infos|hidden and the free-text message is last.
		Set includeErrors, includeWarnings, includeInfo, or includeHidden to include the actual details (all default false).
		Analyzers (NetAnalyzers / IDE rules) run by default; set includeAnalyzers false for a faster compiler-only pass.
		Optional filters narrow the result after compilation (so a filtered call reuses the cached result and recompiles
		nothing) and combine: projectName (exactly as the outline prints the project - the project file name with a
		.csproj extension omitted, others such as .vbproj kept; it matches every loaded target framework of a
		multi-targeted project), filePath (absolute or relative to the solution folder, like find_definition; a
		.razor/.cshtml path also matches the diagnostics reported against it), ids (e.g. ["CS0246","CS0103"],
		case-insensitive), maxResults, and summaryOnly. With summaryOnly=true the body is one line per id instead:
		<id>,<count>,<project>,<path>,<line:col>,<message>, ordered by count descending, carrying the count, one example
		location and that example's message; it covers every severity unless an include flag is set. Counts describe the
		FILTERED set, and every active filter is echoed as a filter=<parameter>:<value> header before them - an empty
		body with a filter= header means the filter matched nothing, not that the solution is clean. A projectName or
		filePath that is not in the solution is error=NotFound with candidate= values; one that exists but has no
		diagnostics returns zero counts. {OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.Truncation} {OutlineDescriptions.ErrorBlock} Prefer this over reading files to hunt for problems, and over running
		`dotnet build`; it returns the compiler's and analyzers' own diagnostics with exact locations.
		Multi-targeted projects report diagnostics across their loaded target frameworks. The Razor compiler's own
		diagnostics (RZxxxx, e.g. an unclosed @code block) are reported against the .razor/.cshtml file. Diagnostic
		ids that exist only to trigger a code fix (they carry no message and accompany a public rule, as IDE0005's
		does) are not listed - fix the public id instead.
		""")]
	public async Task<string> GetDiagnostics(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Include error-severity diagnostics.")] bool includeErrors = false,
		[Description("Include warning-severity diagnostics.")] bool includeWarnings = false,
		[Description("Include info-severity diagnostics.")] bool includeInfo = false,
		[Description("Include hidden-severity diagnostics.")] bool includeHidden = false,
		[Description("Run the project's analyzers (NetAnalyzers / IDE rules) for a richer result; set false for a faster compiler-only pass.")] bool includeAnalyzers = true,
		[Description("Only diagnostics from this project: the name exactly as the outline prints it (the project file name; the .csproj extension is omitted, other extensions such as .vbproj are kept) or that name carrying its extension. Case-insensitive. A multi-targeted project is matched across every loaded target framework.")] string? projectName = null,
		[Description("Only diagnostics located in this file: absolute, or relative to the solution folder (the same spellings find_definition accepts). A .razor/.cshtml path also matches the diagnostics reported against it. Case-insensitive.")] string? filePath = null,
		[Description("Only these diagnostic ids, e.g. [\"CS0246\",\"CS0103\"]; case-insensitive. Omit, or pass an empty list, for every id.")] string[]? ids = null,
		[Description($"Optional cap on the entries listed in the body (id lines when summaryOnly is set); the per-severity header counts stay complete. Default: no cap. {OutlineDescriptions.Truncation}")] int? maxResults = null,
		[Description("Optional. true lists one line per diagnostic id (its count, one example location and the example message) instead of every diagnostic, ordered by count descending then id; it covers every severity unless an include flag is set. Default false.")] bool summaryOnly = false)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		Solution current = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(current);

		// Content filters narrow what is counted and listed. They are resolved (and validated) before any
		// work is queued - a scope that is not in the solution is a NotFound naming values this tool accepts,
		// not an expensive compile followed by an empty result - and applied to the result after the build, so
		// the memoized per-project results and the instance-level cache are reused instead of being recomputed
		// per filter.
		string? projectNameFilter = NormalizeProjectName(projectName);
		if (projectNameFilter is not null)
			projectNameFilter = CanonicalProjectName(current, projectNameFilter);
		string? fullFilePath = string.IsNullOrWhiteSpace(filePath)
			? null
			: SolutionRelativePath.ToAbsolute(solutionDirectory, filePath);
		HashSet<string>? idSet = IdsOrNull(ids);

		if (projectNameFilter is string name && !ProjectNameExists(current, name))
			return OutlineError.Format(
				Error.NotFound(
					$"No project named '{name}' is loaded; pass the name as outlines show it, with or without the .csproj extension.",
					ProjectNameCandidates(current, name)),
				model.Status);

		if (fullFilePath is not null && !PathIsInSolution(current, fullFilePath))
			return OutlineError.Format(
				Error.NotFound(
					$"No file '{filePath!.Trim()}' is compiled or loaded in the solution; pass a path relative to the solution folder or an absolute path.",
					FilePathCandidates(current, solutionDirectory, fullFilePath)),
				model.Status);

		// Fence writes, drain in-flight ones, then build (or reuse the cached result when nothing changed).
		string cacheKey = $"{includeAnalyzers}";
		DiagnosticsResult diagnostics = await instance.RequestDiagnosticsAsync(
			cacheKey,
			(solution, token) => DiagnosticsService.GetAllDiagnosticsAsync(solution, includeAnalyzers, token));

		Solution compiled = diagnostics.Solution;

		// The Razor compiler's own diagnostics come from Roslynk's in-process generator run, not the compilation.
		IReadOnlyList<Diagnostic> razor = instance.Workspace?.Razor.Diagnostics(compiled) ?? [];

		// Analyzers report private trigger ids alongside the public rule (IDE0005's, for one). They have no
		// message and nothing to act on, so they are dropped before the counts as well as the body.
		List<Diagnostic> all = diagnostics.Diagnostics
			.Concat(razor)
			.Where(diagnostic => !CodeActionCatalog.IsPrivateFixTrigger(diagnostic.Id))
			.ToList();

		// The filters reuse the same ProjectOf/display-path functions the body labels with, so a diagnostic is
		// filtered exactly the way it would be printed. With no filters active the list is the unfiltered one.
		List<Diagnostic> filtered = projectNameFilter is null && fullFilePath is null && idSet is null
			? all
			: all.Where(diagnostic => MatchesFilter(diagnostic, compiled, solutionDirectory, projectNameFilter, fullFilePath, idSet)).ToList();

		var wanted = new HashSet<DiagnosticSeverity>();
		if (includeErrors)
			wanted.Add(DiagnosticSeverity.Error);
		if (includeWarnings)
			wanted.Add(DiagnosticSeverity.Warning);
		if (includeInfo)
			wanted.Add(DiagnosticSeverity.Info);
		if (includeHidden)
			wanted.Add(DiagnosticSeverity.Hidden);

		int cap = Math.Max(0, maxResults ?? int.MaxValue);
		List<Diagnostic> items;
		IReadOnlyList<IdSummary> summaries;
		if (summaryOnly)
		{
			items = [];
			summaries = SummarizeById(filtered, wanted, compiled, solutionDirectory);
		}
		else
		{
			summaries = [];
			items = OrderedForListing(
				filtered.Where(diagnostic => wanted.Contains(diagnostic.Severity)),
				compiled,
				solutionDirectory);
		}

		var builder = new OutlineBuilder();
		if (projectNameFilter is string echoedProject)
			builder.Header("filter", $"projectName:{echoedProject}");
		if (fullFilePath is not null)
			builder.Header("filter", $"filePath:{filePath!.Trim()}");
		if (idSet is not null)
			builder.Header("filter", $"ids:{string.Join('|', idSet.Order(StringComparer.Ordinal))}");

		// Counts span the content-filtered set, every severity, independent of the include toggles.
		builder.Header("errors", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		builder.Header("warnings", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning));
		builder.Header("infos", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info));
		builder.Header("hidden", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Hidden));

		int totalRows = summaryOnly ? summaries.Count : items.Count;
		if (totalRows > cap)
		{
			builder.Header("count", totalRows);
			builder.Header("truncated", true);
		}

		builder.Status(instance.CurrentModel.Status);
		builder.BeginBody();

		if (summaryOnly)
		{
			foreach (IdSummary summary in summaries.Take(cap))
				builder.Line(0, SummaryEntryText(summary, solutionDirectory, compiled));

			return builder.ToString();
		}

		IEnumerable<IGrouping<string?, Diagnostic>> byProject = items
			.Take(cap)
			.GroupBy(diagnostic => ProjectOf(diagnostic, compiled))
			.OrderBy(group => group.Key is null)
			.ThenBy(group => group.Key, StringComparer.Ordinal);

		foreach (IGrouping<string?, Diagnostic> project in byProject)
		{
			int fileDepth = 0;
			if (project.Key is string projectLabel)
			{
				builder.Line(0, projectLabel);
				fileDepth = 1;
			}

			FolderFiles.Write(builder, fileDepth, project, diagnostic => FileOf(diagnostic, solutionDirectory), (severityDepth, file) =>
			{
				IEnumerable<IGrouping<DiagnosticSeverity, Diagnostic>> bySeverity = file
					.GroupBy(diagnostic => diagnostic.Severity)
					.OrderByDescending(group => group.Key);

				foreach (IGrouping<DiagnosticSeverity, Diagnostic> severity in bySeverity)
				{
					builder.Line(severityDepth, SeverityLabel(severity.Key));

					IEnumerable<Diagnostic> ordered = severity
						.OrderBy(diagnostic => HasFile(diagnostic.Location) ? 0 : 1)
						.ThenBy(diagnostic => HasFile(diagnostic.Location)
							? diagnostic.Location.GetDisplaySpan().StartLinePosition.Line
							: int.MaxValue)
						.ThenBy(diagnostic => HasFile(diagnostic.Location)
							? diagnostic.Location.GetDisplaySpan().StartLinePosition.Character
							: int.MaxValue);

					foreach (Diagnostic diagnostic in ordered)
						builder.Line(severityDepth + 1, EntryText(diagnostic));
				}
			});
		}

		return builder.ToString();
	}

	/// <summary>
	/// Whether the diagnostic points into a file: a source location, or (for a Razor compiler diagnostic) a
	/// location in a .razor/.cshtml file outside the compilation.
	/// </summary>
	private static bool HasFile(Location location) =>
		location.IsInSource || location.Kind == LocationKind.ExternalFile;

	private static string? ProjectOf(Diagnostic diagnostic, Solution solution) =>
		diagnostic.Location.SourceTree is SyntaxTree tree
			? ProjectName.Of(solution, tree)
			: diagnostic.Location.Kind == LocationKind.ExternalFile && diagnostic.Location.GetLineSpan().Path is { Length: > 0 } path
				? ProjectName.OfPath(solution, path)
				: null;

	private static string FileOf(Diagnostic diagnostic, string? solutionDirectory) =>
		HasFile(diagnostic.Location)
			? SolutionRelativePath.Of(solutionDirectory, diagnostic.Location.GetDisplaySpan().Path)!
			: NoLocationBucket;

	private static string SeverityLabel(DiagnosticSeverity severity) =>
		severity switch
		{
			DiagnosticSeverity.Error => "errors",
			DiagnosticSeverity.Warning => "warnings",
			DiagnosticSeverity.Info => "infos",
			_ => "hidden",
		};

	private static string EntryText(Diagnostic diagnostic)
	{
		string message = OutlineBuilder.Sanitize(diagnostic.GetMessage());

		if (!HasFile(diagnostic.Location))
			return $"{diagnostic.Id},{message}";

		FileLinePositionSpan span = diagnostic.Location.GetDisplaySpan();
		int line = span.StartLinePosition.Line + 1;
		int column = span.StartLinePosition.Character + 1;
		return $"{diagnostic.Id},{line}:{column},{message}";
	}

	/// <summary>
	/// Trims the argument and drops a trailing .csproj extension, so 'Lib' and 'Lib.csproj' match the name the
	/// outline prints (a .csproj project's label has no extension). Other extensions must be passed as printed,
	/// so a trailing .vbproj/.fsproj is kept as-is.
	/// </summary>
	private static string? NormalizeProjectName(string? projectName)
	{
		if (string.IsNullOrWhiteSpace(projectName))
			return null;

		string trimmed = projectName.Trim();
		const string CSharpExtension = ".csproj";
		return trimmed.Length > CSharpExtension.Length && trimmed.EndsWith(CSharpExtension, StringComparison.OrdinalIgnoreCase)
			? trimmed[..^CSharpExtension.Length]
			: trimmed;
	}

	private static HashSet<string>? IdsOrNull(string[]? ids)
	{
		if (ids is not { Length: > 0 })
			return null;

		var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string? id in ids)
		{
			if (!string.IsNullOrWhiteSpace(id))
				wanted.Add(id.Trim());
		}

		// An empty/whitespace list is 'no filter', not 'match nothing'.
		return wanted.Count > 0 ? wanted : null;
	}

	private static bool MatchesFilter(
		Diagnostic diagnostic,
		Solution compiled,
		string? solutionDirectory,
		string? projectNameFilter,
		string? fullFilePath,
		HashSet<string>? idSet)
	{
		// The project filter uses the same ProjectOf label the body groups by, so a diagnostic is filtered
		// exactly the way it would be labelled (WYSIWYG). A diagnostic with no project never matches.
		if (projectNameFilter is not null
			&& (ProjectOf(diagnostic, compiled) is not string project
				|| !string.Equals(project, projectNameFilter, StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}

		// The file filter compares display paths: a .cs diagnostic, a Razor C# diagnostic mapped back through
		// #line, and an RZ* external-file diagnostic all expose the .razor/.cs path FileOf prints.
		if (fullFilePath is not null)
		{
			if (!HasFile(diagnostic.Location))
				return false;

			string displayPath = diagnostic.Location.GetDisplaySpan().Path;
			if (string.IsNullOrEmpty(displayPath))
				return false;

			string candidate = SolutionRelativePath.ToAbsolute(solutionDirectory, displayPath);
			if (!string.Equals(candidate, fullFilePath, StringComparison.OrdinalIgnoreCase))
				return false;
		}

		return idSet is null || idSet.Contains(diagnostic.Id);
	}

	/// <summary>
	/// Orders diagnostics into exactly the order the detail body walks: project (null-last, ordinal), folder,
	/// file, severity descending, has-file, line, column. LINQ sorts are stable, so diagnostics tying on every
	/// key keep the compiled order, matching what the unsorted walk would have printed.
	/// </summary>
	private static List<Diagnostic> OrderedForListing(
		IEnumerable<Diagnostic> diagnostics,
		Solution compiled,
		string? solutionDirectory) =>
		[.. diagnostics
			.OrderBy(diagnostic => ProjectOf(diagnostic, compiled) is null)
			.ThenBy(diagnostic => ProjectOf(diagnostic, compiled), StringComparer.Ordinal)
			.ThenBy(diagnostic => FolderPart(diagnostic, solutionDirectory), StringComparer.Ordinal)
			.ThenBy(diagnostic => FilePart(diagnostic, solutionDirectory), StringComparer.Ordinal)
			.ThenByDescending(diagnostic => diagnostic.Severity)
			.ThenBy(diagnostic => HasFile(diagnostic.Location) ? 0 : 1)
			.ThenBy(diagnostic => HasFile(diagnostic.Location) ? diagnostic.Location.GetDisplaySpan().StartLinePosition.Line : int.MaxValue)
			.ThenBy(diagnostic => HasFile(diagnostic.Location) ? diagnostic.Location.GetDisplaySpan().StartLinePosition.Character : int.MaxValue)];

	private static string? FolderPart(Diagnostic diagnostic, string? solutionDirectory) =>
		OutlinePath.Split(FileOf(diagnostic, solutionDirectory)).Folder;

	private static string FilePart(Diagnostic diagnostic, string? solutionDirectory) =>
		OutlinePath.Split(FileOf(diagnostic, solutionDirectory)).Name;

	private static bool ProjectNameExists(Solution solution, string projectName) =>
		solution.Projects
			.Select(ProjectName.Of)
			.OfType<string>()
			.Any(candidate => string.Equals(candidate, projectName, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Returns the project's own spelling of a case-insensitive name match, so the filter= echo carries the
	/// label the outline prints. A name matching no project is returned unchanged.
	/// </summary>
	private static string CanonicalProjectName(Solution solution, string projectName) =>
		solution.Projects
			.Select(ProjectName.Of)
			.OfType<string>()
			.FirstOrDefault(candidate => string.Equals(candidate, projectName, StringComparison.OrdinalIgnoreCase))
			?? projectName;

	/// <summary>
	/// Distinct project names that contain, or are contained by, the requested name - or all of them when none
	/// do - so a caller can resend one verbatim.
	/// </summary>
	private static IReadOnlyList<string> ProjectNameCandidates(Solution solution, string projectName)
	{
		string[] names = [.. solution.Projects
			.Select(ProjectName.Of)
			.OfType<string>()
			.Distinct(StringComparer.Ordinal)];

		string[] related = [.. names
			.Where(candidate => candidate.Contains(projectName, StringComparison.OrdinalIgnoreCase)
				|| projectName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
			.OrderBy(name => name, StringComparer.Ordinal)];

		IEnumerable<string> result = related.Length > 0 ? related : names.OrderBy(name => name, StringComparer.Ordinal);
		return [.. result.Take(20)];
	}

	private static bool PathIsInSolution(Solution solution, string fullPath)
	{
		// GetDocumentIdsWithFilePath covers additional documents too, so a .razor/.cshtml path resolves.
		foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(fullPath))
		{
			if (solution.GetDocument(id) is not null || solution.GetAdditionalDocument(id) is not null)
				return true;
		}

		return false;
	}

	private static IReadOnlyList<string> FilePathCandidates(Solution solution, string? solutionDirectory, string fullPath)
	{
		string requested = SolutionRelativePath.Of(solutionDirectory, fullPath)!;
		SortedSet<string> all = new(StringComparer.Ordinal);
		SortedSet<string> related = new(StringComparer.Ordinal);

		void Consider(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return;

			string relative = SolutionRelativePath.Of(solutionDirectory, path)!;
			all.Add(relative);
			if (relative.Contains(requested, StringComparison.OrdinalIgnoreCase)
				|| requested.Contains(relative, StringComparison.OrdinalIgnoreCase))
			{
				related.Add(relative);
			}
		}

		foreach (Project project in solution.Projects)
		{
			foreach (Document document in project.Documents)
				Consider(document.FilePath);

			foreach (AdditionalDocument document in project.AdditionalDocuments)
				Consider(document.FilePath);
		}

		IEnumerable<string> result = related.Count > 0 ? related : all;
		return [.. result.Take(20)];
	}

	private sealed record IdSummary(string Id, int Count, Diagnostic Example);

	/// <summary>
	/// Groups the filtered diagnostics by id, keeping each id's first diagnostic in body order as its example.
	/// When no include toggle is set the population is every severity; otherwise the selected ones only.
	/// </summary>
	private static IReadOnlyList<IdSummary> SummarizeById(
		List<Diagnostic> filtered,
		HashSet<DiagnosticSeverity> wanted,
		Solution compiled,
		string? solutionDirectory)
	{
		IEnumerable<Diagnostic> population = wanted.Count == 0
			? filtered
			: filtered.Where(diagnostic => wanted.Contains(diagnostic.Severity));

		var byId = new Dictionary<string, (int Count, Diagnostic Example)>(StringComparer.Ordinal);
		foreach (Diagnostic diagnostic in OrderedForListing(population, compiled, solutionDirectory))
		{
			if (byId.TryGetValue(diagnostic.Id, out (int Count, Diagnostic Example) existing))
				byId[diagnostic.Id] = (existing.Count + 1, existing.Example);
			else
				byId[diagnostic.Id] = (1, diagnostic);
		}

		return [.. byId
			.OrderByDescending(pair => pair.Value.Count)
			.ThenBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => new IdSummary(pair.Key, pair.Value.Count, pair.Value.Example))];
	}

	private static string SummaryEntryText(IdSummary summary, string? solutionDirectory, Solution compiled)
	{
		string message = OutlineBuilder.Sanitize(summary.Example.GetMessage());
		string project = OutlineBuilder.Field(ProjectOf(summary.Example, compiled) ?? "");

		if (!HasFile(summary.Example.Location))
			return $"{summary.Id},{summary.Count},{project},,,{message}";

		FileLinePositionSpan span = summary.Example.Location.GetDisplaySpan();
		string path = OutlineBuilder.Field(SolutionRelativePath.Of(solutionDirectory, span.Path)!);
		return $"{summary.Id},{summary.Count},{project},{path},{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1},{message}";
	}
}
