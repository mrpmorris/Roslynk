using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Documentation;
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
	private readonly ResponseBudget Budget;

	public GetDiagnosticsTool(InstanceRegistry instanceRegistry, DiagnosticsService diagnosticsService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		DiagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Returns compiler/analyzer diagnostics.

		errors/warnings/infos/hidden count all content-filtered severities regardless of include flags.
		The body lists flagged severities; summaryOnly summarizes the flagged severities, or errors,
		warnings and infos when none are flagged. Hidden rows are listed only with includeHidden=true.
		Details: project > folder > file > severity; leaves: <id>,<line:col>,<message>.
		Summary: <id>,<count>,<project>,<path>,<line:col>,<message> per id, ordered by count descending then id.

		projectName/filePath/ids filter after compilation, reusing cached results; active filters echo
		filter=<parameter>:<value>. Empty filtered results do not prove the solution clean.
		Unknown project/file returns NotFound with candidates. Known matches without diagnostics return zeros.
		filePath also accepts a unique path suffix or file name; when that resolves it, resolvedPath=<solution-relative path>
		names the canonical form. Several matching files return Ambiguous with those paths as candidates.
		Private fixer-trigger IDs are omitted; fix the public rule instead (e.g. IDE0005).
		""")]
	[McpServerTool(
		Name = GetDiagnosticsName,
		Title = "Get compiler diagnostics",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetDiagnostics(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("List errors.")] bool includeErrors = false,
		[Description("List warnings.")] bool includeWarnings = false,
		[Description("List info diagnostics.")] bool includeInfo = false,
		[Description("List hidden diagnostics.")] bool includeHidden = false,
		[Description("Run analyzers; false is faster, compiler-only.")] bool includeAnalyzers = true,
		[Description("Project filter: outline name with optional extension, case-insensitive; all loaded target frameworks.")] string? projectName = null,
		[Description("File filter (case-insensitive), including diagnostics mapped to Razor source.")] string? filePath = null,
		[Description("Diagnostic ids (case-insensitive), e.g. [\"CS0246\"]; a JSON-array string such as \"[\\\"CS0246\\\"]\" is also accepted; null/empty matches all.")] string[]? ids = null,
		[Description("Summarize by diagnostic id.")] bool summaryOnly = false,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync();

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status);

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
		bool fileFilterRequested = !string.IsNullOrWhiteSpace(filePath);
		DocumentPathResolution fileResolution = fileFilterRequested
			? DocumentPathResolution.Of(
				current,
				filePath,
				$"No file '{filePath!.Trim()}' is compiled or loaded in the solution; pass a path relative to the solution folder or an absolute path.")
			: new(null, null);
		ResolvedDocumentPath? resolvedFile = fileResolution.Resolved;
		string? fullFilePath = resolvedFile?.AbsolutePath;
		HashSet<string>? idSet = IdsOrNull(ids);

		if (projectNameFilter is string name && !ProjectNameExists(current, name))
			return OutlineError.Format(
				Error.NotFound(
					$"No project named '{name}' is loaded; pass the name as outlines show it, with or without the .csproj extension.",
					ProjectNameCandidates(current, name)),
				model.Status);

		if (fileResolution.Error is Error fileError)
			return OutlineError.Format(fileError, model.Status);

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

		int cap = maxSize > 0 ? maxSize : maxSize < 0 ? 0 : int.MaxValue;
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

		int totalRows = summaryOnly ? summaries.Count : items.Count;
		bool capped = totalRows > cap;

		void RenderBody(OutlineBuilder target, List<OutlineItem>? collect)
		{
			if (summaryOnly)
			{
				foreach (IdSummary summary in summaries.Take(cap))
				{
					string entry = SummaryEntryText(summary, solutionDirectory, compiled);
					target.Line(0, entry);
					collect?.Add(OutlineItem.Single(entry, []));
				}

				return;
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
					target.Line(0, projectLabel);
					fileDepth = 1;
				}

				FolderFiles.Write(target, fileDepth, project, diagnostic => FileOf(diagnostic, solutionDirectory), (severityDepth, file) =>
				{
					IEnumerable<IGrouping<DiagnosticSeverity, Diagnostic>> bySeverity = file
						.GroupBy(diagnostic => diagnostic.Severity)
						.OrderByDescending(group => group.Key);

					foreach (IGrouping<DiagnosticSeverity, Diagnostic> severity in bySeverity)
					{
						target.Line(severityDepth, SeverityLabel(severity.Key));

						IEnumerable<Diagnostic> ordered = severity
							.OrderBy(diagnostic => HasFile(diagnostic.Location) ? 0 : 1)
							.ThenBy(diagnostic => HasFile(diagnostic.Location)
								? diagnostic.Location.GetDisplaySpan().StartLinePosition.Line
								: int.MaxValue)
							.ThenBy(diagnostic => HasFile(diagnostic.Location)
								? diagnostic.Location.GetDisplaySpan().StartLinePosition.Character
								: int.MaxValue);

						foreach (Diagnostic diagnostic in ordered)
						{
							string entry = EntryText(diagnostic);
							target.Line(severityDepth + 1, entry);
							if (collect is not null)
							{
								// The chain is the project line, folder line, file line and severity label the
								// entry renders under.
								string fileLabel = FileOf(diagnostic, solutionDirectory);
								(string? folder, string fileName) = OutlinePath.Split(fileLabel);
								string[] context;
								if (project.Key is string p)
									context = folder is null ? [p, fileName] : [p, folder, fileName];
								else
									context = folder is null ? [fileName] : [folder, fileName];
								collect.Add(OutlineItem.Single(entry, [.. context, SeverityLabel(severity.Key)]));
							}
						}
					}
				});
			}
		}

		var collected = new List<OutlineItem>();
		RenderBody(new OutlineBuilder(), collected);

		PagedRender render = ItemPaging.Render(
			instance.CurrentModel.Status,
			builder =>
			{
				if (projectNameFilter is string echoedProject)
					builder.Header("filter", $"projectName:{echoedProject}");
				if (fullFilePath is not null)
					builder.Header("filter", $"filePath:{filePath!.Trim()}");
				if (resolvedFile is { ResolvedBySuffix: true })
					builder.Header("resolvedPath", resolvedFile.RelativePath);
				if (idSet is not null)
					builder.Header("filter", $"ids:{string.Join('|', idSet.Order(StringComparer.Ordinal))}");

				// Counts span the content-filtered set, every severity, independent of the include toggles.
				builder.Header("errors", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
				builder.Header("warnings", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning));
				builder.Header("infos", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info));
				builder.Header("hidden", filtered.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Hidden));
			},
			capHeaders: capped
				? builder =>
				{
					builder.Header("count", Math.Min(totalRows, cap));
					builder.Header("truncated", true);
				}
				: null,
			renderBody: builder => RenderBody(builder, null),
			items: collected,
			startItem: startItem,
			maxSize: 0,
			budget: Budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => OutlineError.Format(Error.Invalid(invalid.Message), instance.CurrentModel.Status),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
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

	private sealed record IdSummary(string Id, int Count, Diagnostic Example);

	/// <summary>
	/// Groups the filtered diagnostics by id, keeping each id's first diagnostic in body order as its example.
	/// When no include toggle is set the population is every severity except Hidden, matching the flags'
	/// defaults (hidden is opt-in); otherwise the selected ones only.
	/// </summary>
	private static IReadOnlyList<IdSummary> SummarizeById(
		List<Diagnostic> filtered,
		HashSet<DiagnosticSeverity> wanted,
		Solution compiled,
		string? solutionDirectory)
	{
		IEnumerable<Diagnostic> population = wanted.Count == 0
			? filtered.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Hidden)
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
