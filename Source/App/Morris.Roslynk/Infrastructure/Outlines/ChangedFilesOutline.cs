using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Writes the body shared by the write tools (rename, change_signature, remove_unused_usings, apply_code_*):
/// the blank separator then the changed files, de-duplicated and sorted, each nested under its owning project
/// file (name.ext). The caller writes the 'applied' and tool-specific headers first; this only renders the
/// grouped file list.
/// </summary>
public static class ChangedFilesOutline
{
	public static void Write(OutlineBuilder builder, IReadOnlyList<string> changedPaths, Solution solution, string? solutionDirectory)
	{
		builder.BeginBody();

		var byProject = changedPaths
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(path => (Project: ProjectName.OfPath(solution, path), Relative: SolutionRelativePath.Of(solutionDirectory, path) ?? path))
			.GroupBy(entry => entry.Project)
			.OrderBy(group => group.Key is null)
			.ThenBy(group => group.Key, StringComparer.Ordinal);

		foreach (var project in byProject)
		{
			int fileDepth = 0;
			if (project.Key is string projectName)
			{
				builder.Line(0, projectName);
				fileDepth = 1;
			}

			FolderFiles.Write(builder, fileDepth, project, entry => entry.Relative, (_, _) => { });
		}
	}
}
