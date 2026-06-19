using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Owns an <see cref="MSBuildWorkspace"/> and the immutable <see cref="Solution"/> snapshot loaded from
/// a <c>.sln</c> / <c>.slnx</c>. Lifecycle, sessions, and the single-writer lock are layered on top of
/// this by the instance registry; this type is just the load + snapshot.
/// </summary>
public sealed class SolutionWorkspace : IDisposable
{
	private readonly MSBuildWorkspace Workspace;

	public Solution Solution { get; }

	/// <summary>Partial-load failures reported by MSBuild while opening the solution.</summary>
	public IReadOnlyList<string> LoadDiagnostics { get; }

	private SolutionWorkspace(MSBuildWorkspace workspace, Solution solution, IReadOnlyList<string> loadDiagnostics)
	{
		Workspace = workspace;
		Solution = solution;
		LoadDiagnostics = loadDiagnostics;
	}

	public static async Task<SolutionWorkspace> LoadAsync(
		string solutionPath,
		IProgress<ProjectLoadProgress>? progress = null,
		CancellationToken cancellationToken = default)
	{
		if (solutionPath is null)
			throw new ArgumentNullException(nameof(solutionPath));

		MsBuildRegistrar.EnsureRegistered();

		var loadDiagnostics = new ConcurrentBag<string>();
		MSBuildWorkspace workspace = MSBuildWorkspace.Create();

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("load_solution"))
		{
			activity?.SetTag("roslynk.solution.path", ActivityTags.Truncate(solutionPath));

			using (workspace.RegisterWorkspaceFailedHandler(e => loadDiagnostics.Add(e.Diagnostic.Message)))
			{
				Solution solution = await workspace.OpenSolutionAsync(solutionPath, progress, cancellationToken);
				solution = await RazorDocumentGenerator.AugmentAsync(solution, cancellationToken);
				solution = await ExpandMultiTargetProjectsAsync(solution, cancellationToken);
				activity?.SetTag("roslynk.project.count", solution.Projects.Count());
				return new SolutionWorkspace(workspace, solution, loadDiagnostics.ToArray());
			}
		}
	}

	private static async Task<Solution> ExpandMultiTargetProjectsAsync(Solution solution, CancellationToken ct)
	{
		foreach (Project project in solution.Projects.ToArray())
		{
			if (project.FilePath is null) continue;

			string[]? tfms = await ReadTargetFrameworksAsync(project.FilePath);
			if (tfms is null || tfms.Length <= 1) continue;

			string? activeTfm = DetectActiveTfm(project);
			if (activeTfm is null) continue;

			foreach (string tfm in tfms)
			{
				if (string.Equals(tfm, activeTfm, StringComparison.OrdinalIgnoreCase))
					continue;

				var properties = new Dictionary<string, string>
				{
					["TargetFramework"] = tfm
				};

				using MSBuildWorkspace subWorkspace = MSBuildWorkspace.Create(properties);
				Project subProject = await subWorkspace.OpenProjectAsync(project.FilePath, cancellationToken: ct);

				ProjectId newProjectId = ProjectId.CreateNewId();
				ProjectInfo projectInfo = ProjectInfo.Create(
					newProjectId,
					VersionStamp.Create(),
					$"{project.Name}({tfm})",
					subProject.AssemblyName,
					subProject.Language,
					filePath: subProject.FilePath,
					outputFilePath: subProject.OutputFilePath,
					compilationOptions: subProject.CompilationOptions,
					parseOptions: subProject.ParseOptions,
					metadataReferences: subProject.MetadataReferences,
					projectReferences: [],
					analyzerReferences: subProject.AnalyzerReferences);

				solution = solution.AddProject(projectInfo);

				foreach (Document doc in subProject.Documents)
				{
					SourceText text = await doc.GetTextAsync(ct);
					solution = solution.AddDocument(
						DocumentId.CreateNewId(newProjectId),
						doc.Name,
						text,
						doc.Folders,
						doc.FilePath);
				}
			}
		}

		return solution;
	}

	private static string? DetectActiveTfm(Project project)
	{
		if (project.ParseOptions is CSharpParseOptions csharpOptions)
		{
			foreach (string symbol in csharpOptions.PreprocessorSymbolNames)
			{
				if (symbol.StartsWith("NET", StringComparison.Ordinal) &&
					symbol.EndsWith("_0", StringComparison.Ordinal) &&
					symbol.Length > 5)
				{
					string version = symbol[3..^2].ToLowerInvariant();
					return $"net{version}.0";
				}
			}
		}
		return null;
	}

	private static async Task<string[]?> ReadTargetFrameworksAsync(string projectFilePath)
	{
		try
		{
			string content = await File.ReadAllTextAsync(projectFilePath);
			var match = Regex.Match(content,
				@"<TargetFrameworks[^>]*>(.*?)</TargetFrameworks>",
				RegexOptions.IgnoreCase | RegexOptions.Singleline);

			if (!match.Success) return null;

			return match.Groups[1].Value
				.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}
		catch
		{
			return null;
		}
	}

	public void Dispose() => Workspace.Dispose();
}
