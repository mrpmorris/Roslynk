using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Features.Solutions.GetSolutionStatus;

[McpServerToolType]
public sealed class GetSolutionStatusTool
{
	public const string GetSolutionStatusName = "get_solution_status";

	/// <summary>How many load diagnostic messages are listed per solution unless every message is requested.</summary>
	public const int MaxLoadDiagnostics = 20;

	/// <summary>The same limit as text, because an int cannot be concatenated into an attribute's constant description.</summary>
	internal const string MaxLoadDiagnosticsText = "20";

	private readonly InstanceRegistry InstanceRegistry;

	public GetSolutionStatusTool(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	[McpServerTool(
		Name = GetSolutionStatusName,
		Title = "Get loaded-solution status",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Lists the solutions currently loaded by the server. Returns a compact text result, not JSON: a
		blank line, then one line per solution
		'<solutionId>,<status>,<loaded>/<total>' where loaded is how many projects have loaded so
		far (a live count while still Building) and total is the count once known ('?' until the first load
		finishes). A solution whose load reported messages (a skipped or unloadable analyzer or generator,
		a project that failed to evaluate, an SDK that was not found; often the cause of confusing compiler
		errors) is, once it is Ready (never while Building, Updating or Faulted), followed by one indented 'loadDiagnostic=<message>' line per message, so a solution with
		none stays a single line. At most {MaxLoadDiagnosticsText} messages are listed per solution, then an
		indented 'loadDiagnosticsTruncated=<n>' line counts the rest; pass allLoadDiagnostics=true to list
		every message.
		{OutlineDescriptions.Freshness}
		""")]
	public string GetSolutionStatus(
		[Description("Optional. true lists every load diagnostic message of every solution instead of the first " + MaxLoadDiagnosticsText + " per solution. Default false.")] bool allLoadDiagnostics = false)
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
			builder.Line(0, $"{instance.Key.FilePath},{model.Status},{loadedProjects}/{totalText}");

			// Messages are final only once the load is done; a Building/Updating/Faulted solution may still be adding them.
			if (model.Status == SolutionStatus.Ready)
				AppendLoadDiagnostics(builder, instance.Workspace?.LoadDiagnostics ?? [], messageLimit);
		}

		return builder.ToString();
	}

	/// <summary>
	/// Writes one indented 'loadDiagnostic=' line per message (at most <paramref name="limit"/> when given),
	/// then a 'loadDiagnosticsTruncated=' line counting those left out.
	/// </summary>
	internal static void AppendLoadDiagnostics(OutlineBuilder builder, IReadOnlyList<string> diagnostics, int? limit)
	{
		// The workspace collects messages in arrival order from concurrent loaders; sort so the cap is stable.
		string[] messages = diagnostics.Order(StringComparer.Ordinal).ToArray();
		int shown = limit is int cap ? Math.Min(cap, messages.Length) : messages.Length;

		for (int i = 0; i < shown; i++)
			builder.Line(1, $"loadDiagnostic={OutlineBuilder.Sanitize(messages[i])}");

		if (shown < messages.Length)
			builder.Line(1, $"loadDiagnosticsTruncated={messages.Length - shown}");
	}
}
