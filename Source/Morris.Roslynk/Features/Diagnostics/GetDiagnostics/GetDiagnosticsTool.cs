using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

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
	public async Task<GetDiagnosticsResponse> GetDiagnostics(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Optional severities to include: error, warning, info, hidden. Defaults to error and warning.")] string[]? severities = null)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		IReadOnlyList<Diagnostic> all = await DiagnosticsService.GetAllDiagnosticsAsync(instance.Workspace.Solution);

		var counts = new DiagnosticCounts(
			Errors: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error),
			Warnings: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning),
			Infos: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info),
			Hidden: all.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Hidden));

		ImmutableArray<DiagnosticSeverity> wanted = ParseSeverities(severities);

		DiagnosticDto[] items = all
			.Where(diagnostic => wanted.Contains(diagnostic.Severity))
			.OrderByDescending(diagnostic => diagnostic.Severity)
			.ThenBy(diagnostic => diagnostic.Location.SourceTree?.FilePath, StringComparer.OrdinalIgnoreCase)
			.Select(Map)
			.ToArray();

		return new GetDiagnosticsResponse(items, counts);
	}

	private static DiagnosticDto Map(Diagnostic diagnostic)
	{
		FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
		return new DiagnosticDto(
			Id: diagnostic.Id,
			Severity: diagnostic.Severity.ToString(),
			Message: diagnostic.GetMessage(),
			SourcePath: diagnostic.Location.IsInSource ? span.Path : null,
			StartLine: span.StartLinePosition.Line + 1,
			StartColumn: span.StartLinePosition.Character + 1,
			EndLine: span.EndLinePosition.Line + 1,
			EndColumn: span.EndLinePosition.Character + 1);
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
