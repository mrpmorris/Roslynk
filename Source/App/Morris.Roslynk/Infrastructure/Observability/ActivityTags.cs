namespace Morris.Roslynk.Infrastructure.Observability;

/// <summary>
/// Shared conventions for the tags Roslynk attaches to its <see cref="RoslynkActivitySource"/> spans and
/// to MCP request spans.
/// </summary>
public static class ActivityTags
{
	public const string SolutionPathTag = "roslynk.solution.path";
	public const string ProjectCountTag = "roslynk.project.count";
	public const string TargetFrameworkTag = "roslynk.target.framework";
	public const string ConfigurationTag = "roslynk.configuration";
	public const string ResultTag = "roslynk.result";
}
