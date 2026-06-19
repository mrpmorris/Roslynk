using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Workspaces;

public sealed class SolutionWorkspace : IDisposable
{
	private static readonly string[] RelevantProperties = ["EmitCompilerGeneratedFiles", "CompilerGeneratedFilesOutputPath"];

	private readonly MSBuildWorkspace Workspace;

	public Solution Solution { get; }

	public ImmutableDictionary<ProjectId, ProjectModel> ProjectModels { get; }

	public IReadOnlyList<string> LoadDiagnostics { get; }

	private SolutionWorkspace(
		MSBuildWorkspace workspace,
		Solution solution,
		ImmutableDictionary<ProjectId, ProjectModel> projectModels,
		IReadOnlyList<string> loadDiagnostics)
	{
		Workspace = workspace;
		Solution = solution;
		ProjectModels = projectModels;
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

				ImmutableDictionary<ProjectId, ProjectModel> immutableModels = CaptureProjectProperties(solution, workspace.Properties);

				solution = await RazorDocumentGenerator.AugmentAsync(solution, immutableModels, cancellationToken);
				solution = await ExpandMultiTargetProjectsAsync(solution, cancellationToken);
				activity?.SetTag("roslynk.project.count", solution.Projects.Count());
				return new SolutionWorkspace(workspace, solution, immutableModels, loadDiagnostics.ToArray());
			}
		}
	}

	private static ImmutableDictionary<ProjectId, ProjectModel> CaptureProjectProperties(
		Solution solution,
		ImmutableDictionary<string, string> workspaceProperties)
	{
		var result = new Dictionary<ProjectId, ProjectModel>();

		Type? projectInstanceType = GetProjectInstanceType();
		ConstructorInfo? ctor = projectInstanceType?.GetConstructor([
			typeof(string), typeof(IDictionary<string, string>), typeof(object)]);
		MethodInfo? getProp = projectInstanceType?.GetMethod("GetPropertyValue", [typeof(string)]);

		if (ctor is null || getProp is null)
			return result.ToImmutableDictionary();

		foreach (Project project in solution.Projects)
		{
			if (project.FilePath is not string path)
				continue;

			var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			try
			{
				object instance = ctor.Invoke([path, workspaceProperties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase), null]);

				foreach (string name in RelevantProperties)
				{
					string? value = (string?)getProp.Invoke(instance, [name]);
					if (!string.IsNullOrEmpty(value))
						props[name] = value;
				}
			}
			catch
			{
			}

			result[project.Id] = new ProjectModel(project.Id, path, props.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
		}

		return result.ToImmutableDictionary();
	}

	private static Type? GetProjectInstanceType()
	{
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly.GetName().Name == "Microsoft.Build")
			{
				try { return assembly.GetType("Microsoft.Build.Execution.ProjectInstance"); }
				catch { }
			}
		}
		return null;
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
