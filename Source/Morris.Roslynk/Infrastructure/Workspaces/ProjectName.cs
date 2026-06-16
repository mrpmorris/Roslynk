using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// The project file name (just name.ext, e.g. <c>VendmanagerWeb.csproj</c>) that owns a source file or
/// syntax tree, used to label path-bearing output with its owning project. A file compiled into several
/// projects (a multi-targeted project, or a linked/shared file) resolves to the first one; the name.ext form
/// is the same across a multi-targeted project's per-framework instances. A symbol or file with no source
/// project resolves to null, so a metadata/disassembled result carries no project.
/// </summary>
public static class ProjectName
{
	public static string? Of(Project? project) =>
		project?.FilePath is string path ? System.IO.Path.GetFileName(path) : null;

	public static string? Of(Solution solution, SyntaxTree tree) =>
		Of(solution.GetDocument(tree)?.Project);

	public static string? OfPath(Solution solution, string filePath)
	{
		foreach (DocumentId id in solution.GetDocumentIdsWithFilePath(filePath))
			return Of(solution.GetProject(id.ProjectId));

		return null;
	}
}
