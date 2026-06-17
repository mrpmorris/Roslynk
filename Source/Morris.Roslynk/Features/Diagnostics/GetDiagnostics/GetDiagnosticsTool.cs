using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
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
		Returns diagnostics for an opened solution. {OutlineDescriptions.TextNotJson} Per-severity counts are
		always in the header so filtering is never silent; diagnostics nest file -> severity:
		  #errors=<n>
		  #warnings=<n>
		  #infos=<n>
		  #hidden=<n>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs>
		  \t\t\t<severity>
		  \t\t\t\t<id>,<line:col>,<message>
		where severity is the plural group errors|warnings|infos|hidden and the free-text message is last. Errors are always
		included; set includeWarnings, includeInfo, or includeHidden to widen (all default false). Analyzers
		(NetAnalyzers / IDE rules) run by default; set includeAnalyzers false for a faster compiler-only pass.
		{OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over reading files to hunt for problems, and over running
		`dotnet build`; it returns the compiler's and analyzers' own diagnostics with exact locations.
		""")]
	public async Task<string> GetDiagnostics(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Include warning-severity diagnostics. Default false.")] bool includeWarnings = false,
		[Description("Include info-severity diagnostics. Default false.")] bool includeInfo = false,
		[Description("Include hidden-severity diagnostics. Default false.")] bool includeHidden = false,
		[Description("Optional target framework (e.g. net8.0) to limit a multi-targeted project to one compilation.")] string? targetFramework = null,
		[Description("Run the project's analyzers (NetAnalyzers / IDE rules) for a richer result. Default true; set false for a faster compiler-only pass.")] bool includeAnalyzers = true)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<Diagnostic> all = await DiagnosticsService.GetAllDiagnosticsAsync(model.Solution, targetFramework, includeAnalyzers);

		var wanted = new HashSet<DiagnosticSeverity> { DiagnosticSeverity.Error };
		if (includeWarnings)
			wanted.Add(DiagnosticSeverity.Warning);
		if (includeInfo)
			wanted.Add(DiagnosticSeverity.Info);
		if (includeHidden)
			wanted.Add(DiagnosticSeverity.Hidden);

		List<Diagnostic> items = all.Where(diagnostic => wanted.Contains(diagnostic.Severity)).ToList();

		var builder = new OutlineBuilder();
		builder.Header("errors", all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		builder.Header("warnings", all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning));
		builder.Header("infos", all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info));
		builder.Header("hidden", all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Hidden));
		builder.Status(model.Status);
		builder.BeginBody();

		IEnumerable<IGrouping<string?, Diagnostic>> byProject = items
			.GroupBy(diagnostic => ProjectOf(diagnostic, model.Solution))
			.OrderBy(group => group.Key is null)
			.ThenBy(group => group.Key, StringComparer.Ordinal);

		foreach (IGrouping<string?, Diagnostic> project in byProject)
		{
			int fileDepth = 0;
			if (project.Key is string projectName)
			{
				builder.Line(0, projectName);
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
						.OrderBy(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line)
						.ThenBy(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Character);

					foreach (Diagnostic diagnostic in ordered)
						builder.Line(severityDepth + 1, EntryText(diagnostic));
				}
			});
		}

		return builder.ToString();
	}

	private static string? ProjectOf(Diagnostic diagnostic, Solution solution) =>
		diagnostic.Location.SourceTree is SyntaxTree tree ? ProjectName.Of(solution, tree) : null;

	private static string FileOf(Diagnostic diagnostic, string? solutionDirectory) =>
		diagnostic.Location.IsInSource
			? SolutionRelativePath.Of(solutionDirectory, diagnostic.Location.GetLineSpan().Path)!
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

		if (!diagnostic.Location.IsInSource)
			return $"{diagnostic.Id},{message}";

		FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
		int line = span.StartLinePosition.Line + 1;
		int column = span.StartLinePosition.Character + 1;
		return $"{diagnostic.Id},{line}:{column},{message}";
	}
}
