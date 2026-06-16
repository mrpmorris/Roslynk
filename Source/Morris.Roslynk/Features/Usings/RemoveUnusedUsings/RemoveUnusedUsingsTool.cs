using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.Usings.RemoveUnusedUsings;

[McpServerToolType]
public sealed class RemoveUnusedUsingsTool
{
	public const string RemoveUnusedUsingsName = "remove_unused_usings";

	private const string UnnecessaryUsingId = "CS8019";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ApplyPipeline ApplyPipeline;

	public RemoveUnusedUsingsTool(InstanceRegistry instanceRegistry, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[McpServerTool(
		Name = RemoveUnusedUsingsName,
		Title = "Remove unused usings",
		ReadOnly = false,
		Idempotent = true,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		"""
		Removes unnecessary using directives (the compiler's CS8019) across the solution, or in one file when
		documentPath is given; the recurring cleanup after moves and renames. Returns a text result, not JSON:
		'#applied', '#removedCount', '#status' header, a blank line, then one solution-relative
		changed-file path per line. Written atomically through the same safe write path as the other tools. Pass
		checkOnly to preview the changed files without writing.
		""")]
	public async Task<string> RemoveUnusedUsings(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Optional path of a single .cs file to clean (absolute or relative to the solution folder). Omit to clean the whole solution.")] string? documentPath = null,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		string Success(bool applied, IReadOnlyList<string> changed, int removedCount)
		{
			var builder = new OutlineBuilder();
			builder.Header("applied", applied);
			builder.Header("removedCount", removedCount);
			builder.Status(instance.CurrentModel.Status);
			ChangedFilesOutline.Write(builder, changed, solutionDirectory);
			return builder.ToString();
		}

		HashSet<DocumentId>? targetDocuments = null;
		if (documentPath is not null)
		{
			Document? document = ResolveDocument(solution, documentPath);
			if (document is null)
				return Failure(Error.NotFound($"'{documentPath}' is not a solution-compiled .cs document."));
			targetDocuments = [document.Id];
		}

		Solution updated = solution;
		int removed = 0;
		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null)
				continue;

			IEnumerable<IGrouping<SyntaxTree, Diagnostic>> byTree = compilation.GetDiagnostics(cancellationToken)
				.Where(diagnostic => diagnostic.Id == UnnecessaryUsingId && diagnostic.Location.SourceTree is not null)
				.GroupBy(diagnostic => diagnostic.Location.SourceTree!);

			foreach (IGrouping<SyntaxTree, Diagnostic> treeDiagnostics in byTree)
			{
				Document? document = solution.GetDocument(treeDiagnostics.Key);
				if (document is null || !IsEditableSource(document) || (targetDocuments is not null && !targetDocuments.Contains(document.Id)))
					continue;

				SyntaxNode root = await treeDiagnostics.Key.GetRootAsync(cancellationToken);
				UsingDirectiveSyntax[] usings = treeDiagnostics
					.Select(diagnostic => root.FindNode(diagnostic.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>())
					.Where(node => node is not null)
					.Distinct()
					.ToArray()!;

				if (usings.Length == 0)
					continue;

				SyntaxNode newRoot = root.RemoveNodes(usings, SyntaxRemoveOptions.KeepNoTrivia)!;
				updated = updated.WithDocumentSyntaxRoot(document.Id, newRoot);
				removed += usings.Length;
			}
		}

		if (removed == 0)
			return Success(applied: false, [], 0);

		if (checkOnly)
			return Success(applied: false, ApplyPipeline.GetChangedFilePaths(solution, updated), removed);

		IReadOnlyList<string> changed = await ApplyPipeline.ApplyAsync(instance, updated, cancellationToken);
		return Success(applied: true, changed, removed);
	}

	private static readonly string[] GeneratedSuffixes = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs"];

	/// <summary>An on-disk source file we may rewrite; never a generated or obj/bin document.</summary>
	private static bool IsEditableSource(Document document)
	{
		string? path = document.FilePath;
		if (string.IsNullOrEmpty(path) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			return false;
		if (GeneratedSuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
			return false;

		foreach (string segment in path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar))
		{
			if (segment.Equals("obj", StringComparison.OrdinalIgnoreCase) || segment.Equals("bin", StringComparison.OrdinalIgnoreCase))
				return false;
		}

		return true;
	}

	private static Document? ResolveDocument(Solution solution, string path)
	{
		string normalized = path.Replace('/', System.IO.Path.DirectorySeparatorChar).Replace('\\', System.IO.Path.DirectorySeparatorChar);
		string full = SolutionRelativePath.ToAbsolute(SolutionRelativePath.DirectoryOf(solution), normalized);

		Document? suffixMatch = null;
		int suffixMatches = 0;
		foreach (Document document in solution.Projects.SelectMany(project => project.Documents))
		{
			if (document.FilePath is null)
				continue;
			if (string.Equals(document.FilePath, full, StringComparison.OrdinalIgnoreCase))
				return document;
			if (document.FilePath.EndsWith(System.IO.Path.DirectorySeparatorChar + normalized, StringComparison.OrdinalIgnoreCase))
			{
				suffixMatch = document;
				suffixMatches++;
			}
		}

		return suffixMatches == 1 ? suffixMatch : null;
	}
}
