using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Razor;

namespace Morris.Roslynk.Infrastructure.Workspaces;

public sealed class SolutionWorkspace : IDisposable
{
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

	private readonly IReadOnlyList<string> FixedLoadDiagnostics;

	/// <summary>
	/// The load's messages: those fixed at load, plus the tracked analyzers' messages that still apply (a
	/// skipped generator DLL drops out once it is built and attached).
	/// </summary>
	public IReadOnlyList<string> LoadDiagnostics => [.. FixedLoadDiagnostics, .. TrackedAnalyzers.Messages];

	/// <summary>
	/// The analyzer DLLs kept fresh after load: skipped ones that were missing, and those built by a project
	/// of the solution. <see cref="Lifecycle.RoslynInstance"/> refreshes them on use.
	/// </summary>
	public TrackedAnalyzers TrackedAnalyzers { get; }

	/// <summary>The in-process Razor generation for this workspace's solution, for regenerating after edits.</summary>
	public RazorGenerationState Razor { get; }

	private SolutionWorkspace(
		MSBuildWorkspace workspace,
		Solution solution,
		IReadOnlyList<string> loadDiagnostics,
		TrackedAnalyzers trackedAnalyzers,
		RazorGenerationState razor)
	{
		Workspace = workspace;
		Solution = solution;
		FixedLoadDiagnostics = loadDiagnostics;
		TrackedAnalyzers = trackedAnalyzers;
		Razor = razor;
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

				// Shadow-copy remap MUST precede the Razor augmentation: AugmentAsync requests compilations,
				// which run source generators and therefore load every analyzer reference. If the references
				// still use MSBuildWorkspace's default loader at that point, the originals in bin/obj get
				// memory-mapped and locked for the lifetime of the process.
				TrackedAnalyzers trackedAnalyzers;
				using (Activity? shadowActivity = RoslynkActivitySource.Instance.StartActivity("shadow_copy_analyzers"))
				{
					shadowActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));
					(solution, trackedAnalyzers) = UseShadowCopyAnalyzerLoaders(solution, loadDiagnostics);
				}

				var razor = new RazorGenerationState();
				using (Activity? razorActivity = RoslynkActivitySource.Instance.StartActivity("razor_augment"))
				{
					razorActivity?.SetTag(ActivityTags.SolutionPathTag, ActivityTags.Truncate(solutionPath));
					solution = await RazorDocumentGenerator.AugmentAsync(solution, razor, cancellationToken);
				}

				loadActivity?.SetTag(ActivityTags.ProjectCountTag, solution.Projects.Count());
				return new SolutionWorkspace(workspace, solution, loadDiagnostics.ToArray(), trackedAnalyzers, razor);
			}
		}
	}

	private static (Solution Solution, TrackedAnalyzers Tracked) UseShadowCopyAnalyzerLoaders(Solution solution, ConcurrentBag<string> loadDiagnostics)
	{
		// One reference per path per load: projects share analyzer paths heavily (SDK analyzers), and a
		// shared instance avoids re-reflecting over the same assembly per project. Scoped to this load —
		// not static — so a reload after a generator rebuild creates fresh references that observe the
		// new bits through the stamp-keyed shadow loader.
		var referencesByPath = new Dictionary<string, AnalyzerFileReference>(StringComparer.OrdinalIgnoreCase);

		// Paths kept fresh after load (see TrackedAnalyzers): a reference whose DLL did not exist, and a DLL
		// that a project of the solution builds.
		var tracked = new Dictionary<string, TrackedAnalyzer>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, string> producers = ProducersByOutputPath(solution);

		foreach (Project project in solution.Projects)
		{
			if (project.AnalyzerReferences.Count == 0)
				continue;

			var remapped = new List<AnalyzerReference>(project.AnalyzerReferences.Count);
			bool changed = false;

			foreach (AnalyzerReference reference in project.AnalyzerReferences)
			{
				string? fullPath = reference switch
				{
					UnresolvedAnalyzerReference unresolved => AbsolutePath(unresolved.FullPath, project),
					AnalyzerFileReference file => FileStamp.NormalizePath(file.FullPath),
					_ => null,
				};

				if (fullPath is null)
				{
					remapped.Add(reference);
					continue;
				}

				changed = true;
				producers.TryGetValue(fullPath, out string? producer);

				TrackedAnalyzer? entry = null;
				if (reference is UnresolvedAnalyzerReference || producer is not null)
				{
					if (!tracked.TryGetValue(fullPath, out entry))
					{
						entry = new TrackedAnalyzer(fullPath, producer, CreateShadowReference);
						tracked[fullPath] = entry;
					}

					entry.AddConsumer(project);
				}

				if (referencesByPath.TryGetValue(fullPath, out AnalyzerFileReference? shadowReference))
				{
					remapped.Add(shadowReference);
					continue;
				}

				if (entry is null)
				{
					shadowReference = CreateShadowReference(fullPath, loadDiagnostics.Add, [project.Language]);
				}
				else
				{
					// A DLL the design-time build did not find stays out of the compilation until it is built: its
					// entry reports it (naming the project to build) and attaches it on the first use after it
					// appears. One that appeared while the solution was loading is attached now.
					if (FileStamp.Of(fullPath) is not string stamp)
						continue;

					// A tracked DLL reports load failures as its own message, which a later successful load
					// clears. Stamped before loading, so a rebuild landing after this point is seen as one.
					shadowReference = entry.Load(stamp);
					entry.Attached = true;
				}

				referencesByPath[fullPath] = shadowReference;
				remapped.Add(shadowReference);
			}

			if (changed)
				solution = solution.WithProjectAnalyzerReferences(project.Id, remapped);
		}

		return (solution, tracked.Count == 0 ? TrackedAnalyzers.None : new TrackedAnalyzers([.. tracked.Values]));
	}

	/// <summary>
	/// A shadow-copied reference to <paramref name="fullPath"/>, loaded now for each of
	/// <paramref name="languages"/>. A reference that fails to load otherwise vanishes silently:
	/// AnalyzerFileReference reports failures only through its event, and the compilation proceeds without the
	/// reference's analyzers and generators — phantom CS0246s with no visible cause. Forcing the load makes
	/// failures reach <paramref name="reportFailure"/> before the caller snapshots them; the first compilation
	/// would load these assemblies anyway.
	/// </summary>
	private static AnalyzerFileReference CreateShadowReference(string fullPath, Action<string> reportFailure, IEnumerable<string> languages)
	{
		var reference = new AnalyzerFileReference(fullPath, ShadowCopyAnalyzerLoader);
		reference.AnalyzerLoadFailed += (_, e) => reportFailure($"Analyzer load failed for '{fullPath}': {e.Message}");

		foreach (string language in languages)
		{
			_ = reference.GetAnalyzers(language);
			_ = reference.GetGenerators(language);
		}

		return reference;
	}

	/// <summary>
	/// The name of the project that builds each output path of the solution, so an analyzer reference to one
	/// is recognized as a DLL the solution builds. Both the published path (bin) analyzer references resolve
	/// to and the intermediate one (obj) are indexed.
	/// </summary>
	private static Dictionary<string, string> ProducersByOutputPath(Solution solution)
	{
		var producers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (Project project in solution.Projects)
		{
			foreach (string? path in (string?[])[project.OutputFilePath, project.CompilationOutputInfo.AssemblyPath])
			{
				if (!string.IsNullOrEmpty(path))
					producers.TryAdd(FileStamp.NormalizePath(path), project.Name);
			}
		}

		return producers;
	}

	/// <summary>The normalized absolute path of a reference, relative ones taken from the project's directory.</summary>
	private static string AbsolutePath(string path, Project project) =>
		FileStamp.NormalizePath(Path.IsPathRooted(path) || project.FilePath is null
			? path
			: Path.Combine(Path.GetDirectoryName(project.FilePath)!, path));

	public void Dispose() => Workspace.Dispose();
}
