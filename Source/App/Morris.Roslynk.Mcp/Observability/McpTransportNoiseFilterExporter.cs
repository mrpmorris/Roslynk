using OpenTelemetry;
using System.Diagnostics;

namespace Morris.Roslynk.Mcp.Observability;

/// <summary>
/// Drops MCP transport-noise spans before they reach the OTLP endpoint, so the dashboard shows tool
/// calls and their engine spans instead of a wall of protocol overhead: the MCP SDK's own spans for
/// <c>initialize</c>, <c>notifications/initialized</c>, <c>tools/list</c> and <c>ping</c>, plus every
/// ASP.NET Core request span whose request carried no tool call (those POSTs and GETs are exactly the
/// protocol chatter). Wrapping the OTLP trace exporter is the only place a span can still be stopped —
/// the SDK enqueues every ended activity into the batch processor before any AddProcessor callback
/// runs, so processors cannot drop, and a sampler that dropped the request span would take the whole
/// tool-call trace with it.
/// </summary>
public sealed class McpTransportNoiseFilterExporter : BaseExporter<Activity>
{
	private static readonly HashSet<string> NoiseSpanNames = new(StringComparer.Ordinal)
	{
		"initialize",
		"notifications/initialized",
		"tools/list",
		"ping",
	};

	private readonly BaseExporter<Activity> innerExporter;

	public McpTransportNoiseFilterExporter(BaseExporter<Activity> innerExporter)
	{
		ArgumentNullException.ThrowIfNull(innerExporter);
		this.innerExporter = innerExporter;
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
		return innerExporter.Export(new Batch<Activity>(keptArray, keptArray.Length));
	}

	private static bool IsTransportNoise(Activity activity) =>
		(activity.Source.Name == McpToolTraceNaming.McpActivitySourceName && NoiseSpanNames.Contains(activity.DisplayName)) ||
		(activity.Source.Name == McpToolTraceNaming.AspNetCoreActivitySourceName &&
			activity.Kind == ActivityKind.Server &&
			activity.GetTagItem(McpToolTraceNaming.ToolNameTag) is not string);

	protected override void Dispose(bool disposing)
	{
		if (disposing)
			innerExporter.Dispose();
		base.Dispose(disposing);
	}
}
