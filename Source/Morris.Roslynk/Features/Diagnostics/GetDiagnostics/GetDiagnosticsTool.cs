using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.Diagnostics.GetDiagnostics;

[McpServerToolType]
public sealed class GetDiagnosticsTool
{
	public const string GetDiagnosticsName = "get_diagnostics";

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
		"""
		Returns diagnostics for an opened solution. Errors are always included; set includeWarnings,
		includeInfo, or includeHidden to widen the result (all default false, so by default only errors are
		returned). Per-severity counts are always included so filtering is never silent, and errors are
		listed first. Analyzers (NetAnalyzers / IDE rules) run by default for a richer result; set
		includeAnalyzers false to skip them for a faster compiler-only pass. Prefer this over reading files
		to hunt for problems; it returns the compiler's and analyzers' own diagnostics with exact locations.
		Prefer this over actually building the solution using `dotnet build`.
		""")]
	public async Task<GetDiagnosticsResult> GetDiagnostics(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Include warning-severity diagnostics. Default false.")] bool includeWarnings = false,
		[Description("Include info-severity diagnostics. Default false.")] bool includeInfo = false,
		[Description("Include hidden-severity diagnostics. Default false.")] bool includeHidden = false,
		[Description("Optional target framework (e.g. net8.0) to limit a multi-targeted project to one compilation.")] string? targetFramework = null,
		[Description("Run the project's analyzers (NetAnalyzers / IDE rules) for a richer result. Default true; set false for a faster compiler-only pass.")] bool includeAnalyzers = true)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		GetDiagnosticsResult Success(IReadOnlyList<DiagnosticDto> diagnostics, DiagnosticCounts counts) =>
			new(model.SnapshotId, model.Status, error: null, diagnostics, counts);

		GetDiagnosticsResult Failure(Error error) =>
			new(model.SnapshotId, model.Status, error, diagnostics: null, counts: null);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		IReadOnlyList<Diagnostic> all = await DiagnosticsService.GetAllDiagnosticsAsync(model.Solution, targetFramework, includeAnalyzers);

		var counts = new DiagnosticCounts(
			errors: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
			warnings: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning),
			infos: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info),
			hidden: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Hidden));

		var wanted = new HashSet<DiagnosticSeverity> { DiagnosticSeverity.Error };
		if (includeWarnings)
			wanted.Add(DiagnosticSeverity.Warning);
		if (includeInfo)
			wanted.Add(DiagnosticSeverity.Info);
		if (includeHidden)
			wanted.Add(DiagnosticSeverity.Hidden);

		DiagnosticDto[] items = all
			.Where(diagnostic => wanted.Contains(diagnostic.Severity))
			.OrderByDescending(diagnostic => diagnostic.Severity)
			.ThenBy(diagnostic => diagnostic.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
			.Select(Map)
			.ToArray();

		return Success(items, counts);
	}

	private static DiagnosticDto Map(Diagnostic diagnostic)
	{
		FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
		return new DiagnosticDto(
			id: diagnostic.Id,
			severity: diagnostic.Severity.ToString(),
			message: diagnostic.GetMessage(),
			sourcePath: diagnostic.Location.IsInSource ? span.Path : null,
			startLine: span.StartLinePosition.Line + 1,
			startColumn: span.StartLinePosition.Character + 1,
			endLine: span.EndLinePosition.Line + 1,
			endColumn: span.EndLinePosition.Character + 1);
	}
}
