using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Workspaces;

public sealed class SolutionWorkspace : IDisposable
{
	private static readonly string[] RelevantProperties = ["EmitCompilerGeneratedFiles", "CompilerGeneratedFilesOutputPath"];

	// MSBuildWorkspace loads analyzer/source-generator assemblies directly from their build-output
	// path and holds a file lock for the lifetime of the workspace. Because Roslynk keeps solutions
	// loaded, that lock never releases and a concurrent `dotnet build` cannot overwrite the generator's
	// output DLL. A shadow-copy loader copies each analyzer assembly to a temp directory and loads the
	// copy, leaving the originals in bin/obj unlocked.
	private static readonly IAnalyzerAssemblyLoader ShadowCopyAnalyzerLoader =
		new ShadowCopyingAnalyzerAssemblyLoader(
			Path.Combine(Path.GetTempPath(), "Roslynk", "AnalyzerShadowCopy"));

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

		var loadDiagnostics = new ConcurrentBag<string>();

		using (Activity? loadActivity = RoslynkActivitySource.Instance.StartActivity("load_solution"))
		{
			loadActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));

			using (Activity? msbuildActivity = RoslynkActivitySource.Instance.StartActivity("msbuild_register"))
			{
				MsBuildRegistrar.EnsureRegistered();
			}

			MSBuildWorkspace workspace = MSBuildWorkspace.Create();
			Solution solution;

			using (workspace.RegisterWorkspaceFailedHandler(e => loadDiagnostics.Add(e.Diagnostic.Message)))
			{
				using (Activity? openActivity = RoslynkActivitySource.Instance.StartActivity("open_solution_async"))
				{
					openActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));

					var projectSpans = new Dictionary<string, Activity>();
					var loadStart = DateTime.UtcNow;
					var loadSw = Stopwatch.StartNew();
					var previousElapsed = TimeSpan.Zero;

					var trackingProgress = new Progress<ProjectLoadProgress>(p =>
					{
						string key = string.Concat(p.FilePath, "|", p.TargetFramework ?? "");
						TimeSpan currentElapsed = p.ElapsedTime;
						double durationSeconds = (currentElapsed - previousElapsed).TotalSeconds;
						previousElapsed = currentElapsed;

						if (projectSpans.TryGetValue(key, out Activity? prev))
						{
							prev.SetEndTime(loadStart + loadSw.Elapsed);
							prev.Dispose();
						}

						// StartActivity returns null when no ActivityListener is sampling (e.g. no OTEL exporter
						// wired up, as in tests); skip the span in that case rather than dereferencing null.
						Activity? projectActivity = RoslynkActivitySource.Instance.StartActivity("project_loaded", ActivityKind.Internal, new ActivityContext(Activity.Current?.TraceId ?? default, Activity.Current?.SpanId ?? default, ActivityTraceFlags.None));
						if (projectActivity is not null)
						{
							projectActivity.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(p.FilePath));
							projectActivity.SetTag(ActivityTags.TargetFrameworkTag, p.TargetFramework ?? "");
							projectActivity.SetTag("roslynk.load.elapsed", currentElapsed.TotalSeconds);
							projectActivity.SetTag("roslynk.load.duration", durationSeconds);
							projectSpans[key] = projectActivity;
						}

						progress?.Report(p);
					});

					solution = await workspace.OpenSolutionAsync(solutionPath, trackingProgress, cancellationToken);

					foreach (Activity a in projectSpans.Values)
					{
						a.SetEndTime(loadStart + loadSw.Elapsed);
						a.Dispose();
					}

					openActivity?.SetTag(ActivityTags.ProjectCountTag, solution.Projects.Count());
				}

				ImmutableDictionary<ProjectId, ProjectModel> immutableModels;
				using (Activity? propsActivity = RoslynkActivitySource.Instance.StartActivity("capture_project_properties"))
				{
					propsActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));
					immutableModels = CaptureProjectProperties(solution, workspace.Properties);
					propsActivity?.SetTag(ActivityTags.ProjectCountTag, immutableModels.Count);
				}

				using (Activity? razorActivity = RoslynkActivitySource.Instance.StartActivity("razor_augment"))
				{
					razorActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));
					solution = await RazorDocumentGenerator.AugmentAsync(solution, immutableModels, cancellationToken);
				}

				using (Activity? mttActivity = RoslynkActivitySource.Instance.StartActivity("expand_multi_target"))
				{
					mttActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));
					solution = await ExpandMultiTargetProjectsAsync(solution, cancellationToken);
				}

				using (Activity? shadowActivity = RoslynkActivitySource.Instance.StartActivity("shadow_copy_analyzers"))
				{
					shadowActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));
					solution = UseShadowCopyAnalyzerLoaders(solution, loadDiagnostics);
				}

				loadActivity?.SetTag(ActivityTags.ProjectCountTag, solution.Projects.Count());
				return new SolutionWorkspace(workspace, solution, immutableModels, loadDiagnostics.ToArray());
			}
		}
	}

	private static ImmutableDictionary<ProjectId, ProjectModel> CaptureProjectProperties(
		Solution solution,
		ImmutableDictionary<string, string> workspaceProperties)
	{
		var result = new Dictionary<ProjectId, ProjectModel>();

		(Type type, ConstructorInfo ctor, MethodInfo getProp)? found = FindProjectInstanceApi();
		if (found is null)
			return result.ToImmutableDictionary();

		var (projectInstanceType, ctor, getProp) = found.Value;
		int ctorParamCount = ctor.GetParameters().Length;

		foreach (Project project in solution.Projects)
		{
			if (project.FilePath is not string path)
				continue;

			var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

			try
			{
				IDictionary<string, string> globalProps = workspaceProperties.ToDictionary(
					kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

				object instance = ctorParamCount == 2
					? ctor.Invoke([path, globalProps])
					: ctor.Invoke([path, globalProps, null]);

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

	private static (Type, ConstructorInfo, MethodInfo)? FindProjectInstanceApi()
	{
		Assembly? buildAssembly = AppDomain.CurrentDomain.GetAssemblies()
			.FirstOrDefault(a => a.GetName().Name == "Microsoft.Build");

		if (buildAssembly is null) return null;

		Type? type;
		try { type = buildAssembly.GetType("Microsoft.Build.Execution.ProjectInstance"); }
		catch { return null; }

		if (type is null) return null;

		MethodInfo? getProp = type.GetMethod("GetPropertyValue", [typeof(string)]);
		if (getProp is null) return null;

		ConstructorInfo? ctor = type.GetConstructors()
			.FirstOrDefault(c =>
			{
				ParameterInfo[] p = c.GetParameters();
				return p.Length >= 2 &&
					p[0].ParameterType == typeof(string) &&
					p[1].ParameterType == typeof(IDictionary<string, string>);
			});

		if (ctor is null) return null;

		return (type, ctor, getProp);
	}

	private static Solution UseShadowCopyAnalyzerLoaders(Solution solution, ConcurrentBag<string> loadDiagnostics)
	{
		// One reference per path per load: projects share analyzer paths heavily (SDK analyzers), and a
		// shared instance avoids re-reflecting over the same assembly per project. Scoped to this load —
		// not static — so a reload after a generator rebuild creates fresh references that observe the
		// new bits through the stamp-keyed shadow loader.
		var referencesByPath = new Dictionary<string, AnalyzerFileReference>(StringComparer.OrdinalIgnoreCase);

		foreach (Project project in solution.Projects)
		{
			if (project.AnalyzerReferences.Count == 0)
				continue;

			var remapped = new List<AnalyzerReference>(project.AnalyzerReferences.Count);
			bool changed = false;

			foreach (AnalyzerReference reference in project.AnalyzerReferences)
			{
				if (reference is AnalyzerFileReference fileReference)
				{
					if (!referencesByPath.TryGetValue(fileReference.FullPath, out AnalyzerFileReference? shadowReference))
					{
						shadowReference = new AnalyzerFileReference(fileReference.FullPath, ShadowCopyAnalyzerLoader);

						// A reference that fails to load otherwise vanishes silently: AnalyzerFileReference
						// reports failures only through this event, and the compilation proceeds without
						// the reference's analyzers and generators — phantom CS0246s with no visible cause.
						shadowReference.AnalyzerLoadFailed += (_, e) =>
							loadDiagnostics.Add($"Analyzer load failed for '{fileReference.FullPath}': {e.Message}");

						// Force the load now so failures land in LoadDiagnostics before it is snapshotted;
						// the first compilation would load these assemblies anyway.
						_ = shadowReference.GetAnalyzers(project.Language);
						_ = shadowReference.GetGenerators(project.Language);

						referencesByPath[fileReference.FullPath] = shadowReference;
					}

					remapped.Add(shadowReference);
					changed = true;
				}
				else
				{
					remapped.Add(reference);
				}
			}

			if (changed)
				solution = solution.WithProjectAnalyzerReferences(project.Id, remapped);
		}

		return solution;
	}

	// Multi-TFM expansion is left to MSBuildWorkspace / the Roslyn host — no manual per-framework clone.
	private static Task<Solution> ExpandMultiTargetProjectsAsync(Solution solution, CancellationToken ct) =>
		Task.FromResult(solution);

	public void Dispose() => Workspace.Dispose();
}
