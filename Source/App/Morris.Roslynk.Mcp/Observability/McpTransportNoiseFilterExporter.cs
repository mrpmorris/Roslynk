using OpenTelemetry;
using OpenTelemetry.Exporter;
using System.Diagnostics;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Drops MCP transport-noise spans before they reach the OTLP endpoint, so the dashboard shows tool
/// calls and their engine spans instead of a wall of protocol overhead: the MCP SDK's own spans for
/// <c>initialize</c>, <c>tools/list</c>, <c>ping</c>, <c>server/discover</c> and every
/// <c>notifications/*</c> method, plus every ASP.NET Core request span whose request carried no tool
/// call (those POSTs and GETs are exactly the protocol chatter). Filtering inside an
/// <see cref="OtlpTraceExporter"/> subclass is the only place a span can still be stopped — the SDK
/// enqueues every ended activity into the batch processor before any AddProcessor callback runs, so
/// processors cannot drop, and a sampler that dropped the request span would take the whole tool-call
/// trace with it. Deriving rather than wrapping also matters for correctness: the SDK points
/// <see cref="BaseExporter{T}.ParentProvider"/> at the processor's exporter (this instance), and the
/// OTLP exporter reads the trace's resource through that parent — a wrapped inner exporter would never
/// see a parent and would export an empty resource, surfacing everywhere as <c>unknown_service</c>.
/// </summary>
public sealed class McpTransportNoiseFilterExporter : OtlpTraceExporter
{
	private static readonly HashSet<string> NoiseSpanNames = new(StringComparer.Ordinal)
	{
		"initialize",
		"tools/list",
		"ping",
		"server/discover",
	};

	private const string NotificationsSpanPrefix = "notifications/";

	public McpTransportNoiseFilterExporter(OtlpExporterOptions options)
		: base(options)
	{
	}

	public override ExportResult Export(in Batch<Activity> batch)
	{
		List<Activity> kept = [];
		foreach (Activity activity in batch)
		{
			if (!IsTransportNoise(activity))
				kept.Add(activity);
		}

		if (kept.Count == 0)
			return ExportResult.Success;

		Activity[] keptArray = [.. kept];
		return base.Export(new Batch<Activity>(keptArray, keptArray.Length));
	}

	private static bool IsTransportNoise(Activity activity) =>
		(activity.Source.Name == McpToolTraceNaming.McpActivitySourceName &&
			(NoiseSpanNames.Contains(activity.DisplayName) ||
				activity.DisplayName.StartsWith(NotificationsSpanPrefix, StringComparison.Ordinal))) ||
		(activity.Source.Name == McpToolTraceNaming.AspNetCoreActivitySourceName &&
			activity.Kind == ActivityKind.Server &&
			activity.GetTagItem(McpToolTraceNaming.ToolNameTag) is not string);
}
