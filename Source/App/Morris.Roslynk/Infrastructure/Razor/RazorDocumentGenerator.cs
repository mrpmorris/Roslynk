using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Produces the Razor <c>.g.cs</c> documents the workspace does not. The SDK's Razor source generator targets
/// a newer Roslyn than Roslynk loads, so the workspace's analyzer loader refuses it and emits nothing, leaving
/// component partials (the <c>ComponentBase</c> base of every <c>.razor</c>) out of the compilation and the
/// hand-written <c>.razor.cs</c> code-behind riddled with phantom CS0103/CS0115/CS0246. We load the generator
/// from the project's own analyzer reference into the default load context (where it binds against our Roslyn
/// and runs), drive it ourselves, and add the generated sources to the solution as documents so the partials
/// are in the compilation for every tool. Best-effort: if the generator cannot be loaded or run, the project
/// is returned unchanged.
///
/// Two fallback paths handle projects where the SDK's design-time build never registers the generator DLL as an
/// analyzer reference (most <c>Microsoft.NET.Sdk.Web</c> projects):
///
///   - The DLL is searched for in the dotnet SDK directory via <c>MSBuildLocator</c>.
///   - Pre-generated <c>.g.cs</c> files from a previous <c>dotnet build</c> are loaded from
///     <c>obj/{config}/{tfm}/generated/Microsoft.CodeAnalysis.Razor.Compiler/</c>.
///
/// When pre-generated files exist on disk they take precedence and the in-process generator is skipped, avoiding
/// duplicate partial-class definitions in the compilation — but only after the snapshot is verified fresh. The
/// <c>obj</c> output is a build-time artifact MSBuild never prunes: a <c>.g.cs</c> can outlive its deleted
/// <c>.razor</c> source (an orphan), lag behind an edited one (stale), or be missing for a newly added one
/// (incomplete). Trusting such a snapshot poisons the compilation with phantom CS0103/CS0246 in files that no
/// longer exist. An invalid snapshot falls back to the in-process generator; when that is unavailable the
/// snapshot minus provable orphans is used (stale files are kept — an outdated partial binds more references
/// than a missing one).
/// </summary>
public static class RazorDocumentGenerator
{
	private const string RazorCompilerFileName = "Microsoft.CodeAnalysis.Razor.Compiler.dll";
	private const string GeneratorTypeName = "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator";
	private const string GeneratedFolder = "RoslynkRazorGenerated";

	private static readonly ConcurrentDictionary<string, IIncrementalGenerator?> Generators = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Adds the generated Razor documents for every Razor project in the solution, returning the augmented solution.</summary>
	public static async Task<Solution> AugmentAsync(Solution solution, ImmutableDictionary<ProjectId, ProjectModel> projectModels, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		foreach (ProjectId projectId in solution.ProjectIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (solution.GetProject(projectId) is Project project)
				solution = await AugmentProjectAsync(solution, project, projectModels, cancellationToken);
		}

		return solution;
	}

	private static async Task<Solution> AugmentProjectAsync(Solution solution, Project project, ImmutableDictionary<ProjectId, ProjectModel> projectModels, CancellationToken cancellationToken)
	{
		Solution before = solution;
		string? generatedDir = GetRazorGeneratedDirectory(project, projectModels.GetValueOrDefault(project.Id));
		SnapshotAnalysis? snapshot = generatedDir is not null && Directory.Exists(generatedDir)
			? AnalyzeSnapshot(project, generatedDir)
			: null;

		if (snapshot is { IsFresh: true })
		{
			solution = await AddPreGeneratedFilesAsync(solution, project, snapshot.NonOrphanFiles, cancellationToken);
		}
		else
		{
			bool hasGenerator = TryGetGenerator(project, out IIncrementalGenerator? generator);
			bool hasRazorInputs = HasRazorInputs(project) || HasRazorFilesOnDisk(project);
			if (hasGenerator && hasRazorInputs)
				solution = await RunGeneratorAsync(solution, project, generator!, cancellationToken);
			else if (snapshot is not null)
				// No generator to fall back to: use the snapshot minus provable orphans. Stale files are
				// kept deliberately — an outdated component partial still declares the class and its
				// members, so keeping it binds far more references than omitting it; only files whose
				// source is gone poison the compilation with symbols that no longer exist anywhere.
				solution = await AddPreGeneratedFilesAsync(solution, project, snapshot.NonOrphanFiles, cancellationToken);
		}

		// Once our documents own the generation, the SDK generator must not also run natively: newer
		// Roslyn loads it where older versions refused, and its source-generated copy of every component
		// partial would duplicate ours (CS0102/CS0111) with references binding to the immutable copy —
		// breaking rename and diagnostics. Removing the analyzer reference suppresses the native run.
		if (!ReferenceEquals(before, solution))
			solution = RemoveNativeRazorGenerator(solution, project.Id);

		return solution;
	}

	private static Solution RemoveNativeRazorGenerator(Solution solution, ProjectId projectId)
	{
		if (solution.GetProject(projectId) is not Project project)
			return solution;

		foreach (AnalyzerReference reference in project.AnalyzerReferences)
		{
			if (reference.FullPath is string path && path.EndsWith(RazorCompilerFileName, StringComparison.OrdinalIgnoreCase))
				solution = solution.RemoveAnalyzerReference(projectId, reference);
		}

		return solution;
	}

	private static async Task<Solution> RunGeneratorAsync(Solution solution, Project project, IIncrementalGenerator generator, CancellationToken cancellationToken)
	{
		try
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null || project.ParseOptions is not CSharpParseOptions parseOptions)
				return solution;

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				generators: [generator.AsSourceGenerator()],
				additionalTexts: project.AnalyzerOptions.AdditionalFiles,
				parseOptions: parseOptions,
				optionsProvider: project.AnalyzerOptions.AnalyzerConfigOptionsProvider);

			GeneratorDriverRunResult result = driver.RunGenerators(compilation, cancellationToken).GetRunResult();

			string projectDirectory = project.FilePath is string filePath
				? System.IO.Path.GetDirectoryName(filePath)!
				: AppContext.BaseDirectory;

			foreach (GeneratorRunResult run in result.Results)
			{
				foreach (GeneratedSourceResult source in run.GeneratedSources)
				{
					string generatedPath = System.IO.Path.Combine(projectDirectory, "obj", GeneratedFolder, source.HintName);
					if (!solution.GetDocumentIdsWithFilePath(generatedPath).IsEmpty)
						continue;

					DocumentId documentId = DocumentId.CreateNewId(project.Id, source.HintName);
					solution = solution.AddDocument(documentId, source.HintName, source.SourceText, filePath: generatedPath);
				}
			}

			return solution;
		}
		catch (Exception)
		{
			return solution;
		}
	}

	/// <summary>
	/// Classifies a pre-generated snapshot against the .razor/.cshtml sources it was generated from.
	/// <c>NonOrphanFiles</c> are the .g.cs matched to an existing source (a hint name that maps back to
	/// no known source is an orphan — its .razor was deleted, and MSBuild never prunes obj output).
	/// <c>IsFresh</c> additionally requires that no source is newer than its .g.cs, that every current
	/// source has a .g.cs, and that no directive file (_Imports.razor etc., whose edits change every
	/// component's generated code without producing output of their own) is newer than the snapshot.
	/// <para>
	/// Matching runs source→hint-name, never the reverse: the generator flattens paths into hint names
	/// with underscores, which cannot be un-flattened unambiguously (Views/Shared/_Layout.cshtml and a
	/// literal Views_Shared__Layout.cshtml collide). Computing the expected hint name for each known
	/// source is deterministic, so a .g.cs is an orphan exactly when no source claims it.
	/// </para>
	/// </summary>
	private sealed record SnapshotAnalysis(bool IsFresh, IReadOnlyList<string> NonOrphanFiles);

	private static SnapshotAnalysis AnalyzeSnapshot(Project project, string generatedDir)
	{
		var nonOrphanFiles = new List<string>();
		bool fresh = true;

		try
		{
			string projectDir = project.FilePath is string projectFilePath
				? System.IO.Path.GetDirectoryName(projectFilePath)!
				: string.Empty;
			if (projectDir.Length == 0)
				return new SnapshotAnalysis(false, nonOrphanFiles);

			var sourcesByHintKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var componentSources = new List<string>();
			var directiveSources = new List<string>();

			foreach (string source in EnumerateRazorSources(project, projectDir))
			{
				if (IsDirectiveOnly(source))
				{
					directiveSources.Add(source);
					continue;
				}

				componentSources.Add(source);
				string relative = System.IO.Path.GetRelativePath(projectDir, source);

				// Folder-preserved layout: relative folders survive, only the file name is flattened.
				string relativeDir = System.IO.Path.GetDirectoryName(relative) ?? "";
				sourcesByHintKey[string.Concat(relativeDir, "|", FlattenToHintName(System.IO.Path.GetFileName(relative)))] = source;

				// Flat layout (older SDKs): the whole relative path is flattened into the file name.
				sourcesByHintKey[string.Concat("|", FlattenToHintName(relative))] = source;
			}

			var matchedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			DateTime oldestGenerated = DateTime.MaxValue;

			foreach (string file in Directory.EnumerateFiles(generatedDir, "*.g.cs", SearchOption.AllDirectories))
			{
				string fileName = System.IO.Path.GetFileName(file);
				if (!fileName.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase) &&
					!fileName.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase))
				{
					// Not a razor/cshtml output; keep it without letting it decide snapshot validity.
					nonOrphanFiles.Add(file);
					continue;
				}

				// The generator emits under a folder named after itself
				// (Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator); the project-relative
				// structure starts beneath it.
				string relativeDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(generatedDir, file)) ?? "";
				string[] segments = relativeDir.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
				if (segments.Length > 0 && segments[0].StartsWith("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase))
					segments = segments[1..];

				string? source = sourcesByHintKey.GetValueOrDefault(
					string.Concat(System.IO.Path.Combine(segments), "|", fileName));
				if (source is null && segments.Length == 0)
					source = sourcesByHintKey.GetValueOrDefault(string.Concat("|", fileName));

				if (source is null)
				{
					fresh = false;
					continue;
				}

				nonOrphanFiles.Add(file);
				matchedSources.Add(source);

				DateTime generatedAt = File.GetLastWriteTimeUtc(file);
				if (generatedAt < oldestGenerated)
					oldestGenerated = generatedAt;

				// Stale: the source was edited after the last build emitted this file.
				if (File.GetLastWriteTimeUtc(source) > generatedAt)
					fresh = false;
			}

			// Incomplete: a source added since the last build has no .g.cs, so its component partial
			// would be missing from the compilation.
			if (componentSources.Any(source => !matchedSources.Contains(source)))
				fresh = false;

			// A directive file edited after the snapshot invalidates every emitted file at once.
			if (directiveSources.Any(directive => File.GetLastWriteTimeUtc(directive) > oldestGenerated))
				fresh = false;
		}
		catch (Exception)
		{
			fresh = false;
		}

		return new SnapshotAnalysis(fresh, nonOrphanFiles);
	}

	/// <summary>
	/// The hint name the razor generator derives from a path: every character that is not a letter or
	/// digit becomes an underscore, and <c>.g.cs</c> is appended (Pages/_Host.cshtml → Pages__Host_cshtml.g.cs).
	/// </summary>
	private static string FlattenToHintName(string path) =>
		string.Concat(path.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".g.cs";

	private static bool IsDirectiveOnly(string path)
	{
		string name = System.IO.Path.GetFileName(path);
		return string.Equals(name, "_Imports.razor", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "_ViewImports.cshtml", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "_ViewStart.cshtml", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Enumerates the project's .razor/.cshtml files. The workspace's additional documents are
	/// authoritative when present (they include linked files outside the project directory and respect
	/// exclusions); the disk scan covers projects whose design-time build registers no additional files.
	/// </summary>
	private static IEnumerable<string> EnumerateRazorSources(Project project, string projectDir)
	{
		var fromProject = project.AdditionalDocuments
			.Select(document => document.FilePath)
			.OfType<string>()
			.Where(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
				|| path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
			.ToArray();

		if (fromProject.Length > 0)
			return fromProject;

		try
		{
			return Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories)
				.Concat(Directory.EnumerateFiles(projectDir, "*.cshtml", SearchOption.AllDirectories))
				.Where(file =>
				{
					// Build artifacts under bin/obj are not project sources.
					string relative = System.IO.Path.GetRelativePath(projectDir, file);
					string firstSegment = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
					return !string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase);
				})
				.ToArray();
		}
		catch
		{
			return [];
		}
	}

	private static async Task<Solution> AddPreGeneratedFilesAsync(Solution solution, Project project, IReadOnlyList<string> files, CancellationToken cancellationToken)
	{
		try
		{
			foreach (string file in files)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (!solution.GetDocumentIdsWithFilePath(file).IsEmpty)
					continue;

				string content = await File.ReadAllTextAsync(file, cancellationToken);
				SourceText text = SourceText.From(content);
				string hintName = System.IO.Path.GetFileName(file);
				DocumentId documentId = DocumentId.CreateNewId(project.Id, hintName);
				solution = solution.AddDocument(documentId, hintName, text, filePath: file);
			}
		}
		catch (Exception)
		{
		}

		return solution;
	}

	private static string? GetRazorGeneratedDirectory(Project project, ProjectModel? projectModel)
	{
		if (project.FilePath is not string projectFilePath || project.OutputFilePath is not string outputFilePath)
			return null;

		string projectDir = System.IO.Path.GetDirectoryName(projectFilePath)!;
		string? outputDir = System.IO.Path.GetDirectoryName(outputFilePath);

		if (outputDir is null)
			return null;

		// If EmitCompilerGeneratedFiles is explicitly false, skip scanning — no files exist.
		if (projectModel?.CapturedProperties.TryGetValue("EmitCompilerGeneratedFiles", out string? emit) == true
			&& !string.Equals(emit, "true", StringComparison.OrdinalIgnoreCase))
			return null;

		// Respect a custom CompilerGeneratedFilesOutputPath if set.
		if (projectModel?.CapturedProperties.TryGetValue("CompilerGeneratedFilesOutputPath", out string? customPath) == true
			&& !string.IsNullOrWhiteSpace(customPath))
		{
			string resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDir, customPath));
			string candidate = System.IO.Path.Combine(resolved, "Microsoft.CodeAnalysis.Razor.Compiler");
			if (Directory.Exists(candidate))
				return candidate;
		}

		string relativeOutput = System.IO.Path.GetRelativePath(projectDir, outputDir);
		string[] parts = relativeOutput.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

		if (parts.Length >= 2 && string.Equals(parts[0], "bin", StringComparison.OrdinalIgnoreCase))
		{
			string configuration = parts[1];
			string targetFramework = parts.Length > 2 ? parts[2] : "";
			string path = System.IO.Path.Combine(projectDir, "obj", configuration, targetFramework, "generated", "Microsoft.CodeAnalysis.Razor.Compiler");
			if (Directory.Exists(path))
				return path;
		}

		if (parts.Length >= 1)
		{
			string configuration = parts[0];
			string path = System.IO.Path.Combine(projectDir, "obj", configuration, "generated", "Microsoft.CodeAnalysis.Razor.Compiler");
			if (Directory.Exists(path))
				return path;
		}

		string objDir = System.IO.Path.Combine(projectDir, "obj");
		if (Directory.Exists(objDir))
		{
			var candidates = Directory.EnumerateDirectories(objDir, "Microsoft.CodeAnalysis.Razor.Compiler", SearchOption.AllDirectories).ToArray();
			if (candidates.Length > 0)
				return System.IO.Path.GetDirectoryName(candidates[0]);
		}

		return null;
	}

	private static bool HasRazorInputs(Project project) =>
		project.AdditionalDocuments.Any(document =>
			document.FilePath is string path
			&& (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)));

	private static bool HasRazorFilesOnDisk(Project project)
	{
		if (project.FilePath is not string projectFilePath)
			return false;

		string projectDir = System.IO.Path.GetDirectoryName(projectFilePath)!;
		try
		{
			return Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories).Any()
				|| Directory.EnumerateFiles(projectDir, "*.cshtml", SearchOption.AllDirectories).Any();
		}
		catch
		{
			return false;
		}
	}

	private static bool TryGetGenerator(Project project, out IIncrementalGenerator? generator)
	{
		generator = null;

		var reference = project.AnalyzerReferences.FirstOrDefault(candidate =>
			candidate.FullPath is string path && path.EndsWith(RazorCompilerFileName, StringComparison.OrdinalIgnoreCase));

		string? razorPath = reference?.FullPath;
		razorPath ??= FindRazorCompilerInSdkDirectory();

		if (razorPath is null)
			return false;

		generator = Generators.GetOrAdd(razorPath, Load);
		return generator is not null;
	}

	private static string? FindRazorCompilerInSdkDirectory()
	{
		try
		{
			if (!MSBuildLocator.IsRegistered)
				return null;

			string msbuildPath = MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault()?.MSBuildPath ?? string.Empty;

			string[] candidates =
			[
				System.IO.Path.Combine(msbuildPath, "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators", RazorCompilerFileName),
				System.IO.Path.Combine(msbuildPath, "Sdks", "Microsoft.NET.Sdk.Razor", "tools", RazorCompilerFileName),
				System.IO.Path.Combine(msbuildPath, "..", "..", "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators", RazorCompilerFileName),
				System.IO.Path.Combine(msbuildPath, "..", "..", "Sdks", "Microsoft.NET.Sdk.Razor", "tools", RazorCompilerFileName),
			];

			foreach (string candidate in candidates)
			{
				string fullPath = System.IO.Path.GetFullPath(candidate);
				if (File.Exists(fullPath))
					return fullPath;
			}

			string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
			string sdkRoot = System.IO.Path.Combine(programFiles, "dotnet", "sdk");
			if (Directory.Exists(sdkRoot))
			{
				foreach (string versionDir in Directory.EnumerateDirectories(sdkRoot))
				{
					string path = System.IO.Path.Combine(versionDir, "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators", RazorCompilerFileName);
					if (File.Exists(path))
						return path;
				}
			}
		}
		catch (Exception)
		{
		}

		return null;
	}

	private static IIncrementalGenerator? Load(string razorPath)
	{
		try
		{
			Assembly assembly = Assembly.LoadFrom(razorPath);
			return assembly.GetType(GeneratorTypeName) is Type type
				? Activator.CreateInstance(type) as IIncrementalGenerator
				: null;
		}
		catch (Exception)
		{
			return null;
		}
	}
}
