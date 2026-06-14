using System.Collections.Immutable;
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

	private static readonly ImmutableArray<DiagnosticSeverity> DefaultSeverities =
		[DiagnosticSeverity.Error, DiagnosticSeverity.Warning];

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
		Returns compiler diagnostics for an opened solution. Defaults to errors and warnings; pass
		'severities' (error, warning, info, hidden) to widen or narrow. Per-severity counts are always
		included so filtering is never silent, and errors are listed before warnings.
		""")]
	public async Task<GetDiagnosticsResult> GetDiagnostics(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Optional severities to include: error, warning, info, hidden. Defaults to error and warning.")] string[]? severities = null,
		[Description("Optional target framework (e.g. net8.0) to limit a multi-targeted project to one compilation.")] string? targetFramework = null,
		[Description("Also run the project's analyzers (NetAnalyzers etc.) — richer (CA/IDE diagnostics) but slower. Default false.")] bool includeAnalyzers = false)
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

		ImmutableArray<DiagnosticSeverity> wanted = ParseSeverities(severities);

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

	private static ImmutableArray<DiagnosticSeverity> ParseSeverities(string[]? severities)
	{
		if (severities is null || severities.Length == 0)
			return DefaultSeverities;

		ImmutableArray<DiagnosticSeverity>.Builder parsed = ImmutableArray.CreateBuilder<DiagnosticSeverity>();
		foreach (string severity in severities)
		{
			if (Enum.TryParse(severity, ignoreCase: true, out DiagnosticSeverity value))
				parsed.Add(value);
		}

		return parsed.Count == 0 ? DefaultSeverities : parsed.ToImmutable();
	}
}
