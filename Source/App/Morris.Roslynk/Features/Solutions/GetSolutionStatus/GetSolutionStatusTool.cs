using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

[McpServerToolType]
public sealed class GetSolutionStatusTool
{
	public const string GetSolutionStatusName = "get_solution_status";

	/// <summary>How many load (and watch) diagnostic messages are listed per solution unless every message is requested.</summary>
	public const int MaxLoadDiagnostics = 20;

	/// <summary>The same limit as text, because an int cannot be concatenated into an attribute's constant description.</summary>
	internal const string MaxLoadDiagnosticsText = "20";

	private readonly InstanceRegistry InstanceRegistry;

	public GetSolutionStatusTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[Description("""
		Lists loaded solutions: <solutionId> (absolute path with forward slashes),<status>,<loaded>/<total>.
		loaded tracks Building progress; total='?' until known after initial loading.

		Ready solutions add indented loadDiagnostic=<message> for analyzer/generator load failures,
		project evaluation failures or missing SDKs, which may explain compiler errors.
		Other states omit messages. Up to 20 per solution, then loadDiagnosticsTruncated=<n>.

		Any state adds indented watchDiagnostic=<message> when the file watcher failed in a way a
		reload would not fix (e.g. the OS watch limit): changes on disk may then not be picked up.
		Up to 20 per solution, then watchDiagnosticsTruncated=<n>; beyond 100 the text is not kept.
		""")]
	[McpServerTool(
		Name = GetSolutionStatusName,
		Title = "Get loaded-solution status",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public string GetSolutionStatus(
		[Description("Include all load and watch diagnostics.")] bool allLoadDiagnostics = false)
	{
		List<RoslynInstance> instances = InstanceRegistry.LoadedInstances().ToList();
		int? messageLimit = allLoadDiagnostics ? null : MaxLoadDiagnostics;

		var builder = new OutlineBuilder();
		builder.BeginBody();

		foreach (RoslynInstance instance in instances)
		{
			SolutionModel model = instance.CurrentModel;
			int? totalProjects = model.Solution?.Projects.Count();
			int loadedProjects = model.Status == SolutionStatus.Ready && totalProjects is int total
				? total
				: instance.LoadedProjects;

			string totalText = totalProjects?.ToString() ?? "?";
			builder.Line(0, $"{instance.Key.SolutionId},{model.Status},{loadedProjects}/{totalText}");

			// Messages are final only once the load is done; a Building/Updating/Faulted solution may still be adding them.
			if (model.Status == SolutionStatus.Ready)
				AppendMessages(builder, "loadDiagnostic", instance.Workspace?.LoadDiagnostics ?? [], messageLimit);

			WatchDiagnosticsSnapshot watch = instance.WatchDiagnostics;
			AppendMessages(builder, "watchDiagnostic", watch.Messages, messageLimit, watch.Dropped);
		}

		return builder.ToString();
	}

	/// <summary>
	/// Writes one indented '<paramref name="key"/>=' line per message (at most <paramref name="limit"/> when
	/// given), then a '<paramref name="key"/>sTruncated=' line counting those left out, including
	/// <paramref name="alreadyDropped"/> messages the source did not keep.
	/// </summary>
	internal static void AppendMessages(OutlineBuilder builder, string key, IReadOnlyList<string> diagnostics, int? limit, int alreadyDropped = 0)
	{
		// Messages arrive in whatever order concurrent loaders and watchers report them; sort so the cap is stable.
		string[] messages = diagnostics.Order(StringComparer.Ordinal).ToArray();
		int shown = limit is int cap ? Math.Min(cap, messages.Length) : messages.Length;

		for (int i = 0; i < shown; i++)
			builder.Line(1, $"{key}={OutlineBuilder.Sanitize(messages[i])}");

		int truncated = messages.Length - shown + alreadyDropped;
		if (truncated > 0)
			builder.Line(1, $"{key}sTruncated={truncated}");
	}
}
