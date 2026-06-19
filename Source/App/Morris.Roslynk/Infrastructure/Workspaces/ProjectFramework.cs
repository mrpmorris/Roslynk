using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Reads a project's target framework from its name. <see cref="SolutionWorkspace"/>
/// expands multi-targeted projects by creating additional <see cref="Project"/> instances
/// with a <c>Name(tfm)</c> suffix (e.g. <c>MultiLib(net8.0)</c>); a project with no suffix
/// is either single-target or the default-TFM instance, so its framework is reported as null.
/// </summary>
public static class ProjectFramework
{
	public static string? Of(Project project)
	{
		string name = project.Name;
		int open = name.LastIndexOf('(');
		if (open >= 0 && name.EndsWith(')'))
			return name[(open + 1)..^1];

		return null;
	}

	/// <summary>True if <paramref name="project"/> should be included for the requested framework (null = any).</summary>
	public static bool Matches(Project project, string? targetFramework) =>
		targetFramework is null || string.Equals(Of(project), targetFramework, StringComparison.OrdinalIgnoreCase);
}
