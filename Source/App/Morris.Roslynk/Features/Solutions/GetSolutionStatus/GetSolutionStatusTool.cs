using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

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
		project evaluation failures or missing SDKs, which may explain compiler errors, then
		loadWarning=<code>: <message> for NuGet restore warnings (NU1902/NU1903/NU1510 ...) that
		MSBuild reports as failures although the project loaded.
		Other states omit messages. Up to 20 of each per solution, then loadDiagnosticsTruncated=<n>
		and loadWarningsTruncated=<n>.

		Any state adds indented watchDiagnostic=<message> when the file watcher failed in a way a
		reload would not fix (e.g. the OS watch limit): changes on disk may then not be picked up.
		Up to 20 per solution, then watchDiagnosticsTruncated=<n>; beyond 100 the text is not kept.

		solutionId restricts the output to that one loaded solution (the id open_solution echoed, or
		its absolute path in either slash form); omit it to list every loaded solution. An id naming
		no loaded solution returns NotFound with candidate=<solutionId> lines.
		""")]
	[McpServerTool(
		Name = GetSolutionStatusName,
		Title = "Get loaded-solution status",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public string GetSolutionStatus(
		[Description("Include all load and watch diagnostics.")] bool allLoadDiagnostics = false,
		[Description("Optional: restrict the output to one loaded solution. Null lists every loaded solution.")] string? solutionId = null)
	{
		if (solutionId is not null && string.IsNullOrWhiteSpace(solutionId))
			return OutlineError.Format(
				Error.Invalid("'solutionId' must name a loaded solution when given; omit it to list every loaded solution."),
				SolutionStatus.Ready);

		List<RoslynInstance> instances = InstanceRegistry.LoadedInstances().ToList();

		if (solutionId is not null)
		{
			SolutionKey requested;
			try
			{
				requested = SolutionKey.For(solutionId);
			}
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
			{
				return OutlineError.Format(
					Error.Invalid($"'solutionId' is not a usable path: {exception.Message}"),
					SolutionStatus.Ready);
			}

			// SolutionKey equality accepts the echoed id and an absolute path in either slash form.
			List<RoslynInstance> matching = instances.Where(instance => instance.Key.Equals(requested)).ToList();
			if (matching.Count == 0)
			{
				string[] candidates = instances
					.Select(instance => instance.Key.SolutionId)
					.Order(StringComparer.Ordinal)
					.ToArray();
				return OutlineError.Format(
					Error.NotFound($"No solution is loaded for '{solutionId}'; omit solutionId to list every loaded solution.", candidates),
					SolutionStatus.Ready);
			}

			instances = matching;
		}

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
			{
				SolutionWorkspace? workspace = instance.Workspace;
				AppendMessages(builder, "loadDiagnostic", workspace?.LoadDiagnostics ?? [], messageLimit);
				AppendMessages(builder, "loadWarning", workspace?.LoadWarnings ?? [], messageLimit);
			}

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
