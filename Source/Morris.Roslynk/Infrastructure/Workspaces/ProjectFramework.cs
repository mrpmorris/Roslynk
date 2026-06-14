using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Reads a project's target framework. MSBuildWorkspace loads a multi-targeted project as one
/// <see cref="Project"/> per framework and names them <c>Name(tfm)</c> (e.g. <c>MultiLib(net8.0)</c>);
/// a single-targeted project has no suffix, so its framework is reported as null.
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
