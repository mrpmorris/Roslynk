using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Finds and validates the Razor output a previous <c>dotnet build</c> left on disk
/// (<c>EmitCompilerGeneratedFiles=true</c>). That output is a build artifact MSBuild never prunes: a <c>.g.cs</c>
/// can outlive its deleted <c>.razor</c> source (an orphan), lag behind an edited one (stale), or be missing
/// for a newly added one (incomplete). Trusting such a snapshot poisons the compilation with phantom
/// CS0103/CS0246 in files that no longer exist, so it is only used once verified fresh.
/// </summary>
internal static class RazorSnapshot
{
	private const string GeneratorAssemblyFolder = "Microsoft.CodeAnalysis.Razor.Compiler";

	/// <summary>
	/// <c>Files</c> are the snapshot's <c>.g.cs</c> files matched to an existing source, with the hint name the
	/// generator gave each (its path below the generator's folder). <c>IsFresh</c> additionally requires that no
	/// input is newer than the oldest of them and that every component source has one.
	/// </summary>
	internal sealed record Analysis(bool IsFresh, IReadOnlyList<(string Path, string HintName)> Files);

	/// <summary>
	/// The project's Razor snapshot directory, or null when there is none. The compiler writes generator output
	/// under <c>CompilerGeneratedFilesOutputPath</c>, which defaults to <c>$(IntermediateOutputPath)generated</c>;
	/// both come from the project's own compilation output info, so the right configuration and target framework
	/// are found in every layout (artifacts output and a custom <c>BaseIntermediateOutputPath</c> included).
	/// </summary>
	public static string? DirectoryFor(Project project)
	{
		foreach (string candidate in Candidates(project))
		{
			if (Directory.Exists(candidate))
				return candidate;
		}

		return null;
	}

	private static IEnumerable<string> Candidates(Project project)
	{
		string? projectDirectory = project.FilePath is string filePath ? System.IO.Path.GetDirectoryName(filePath) : null;
		CompilationOutputInfo outputInfo = project.CompilationOutputInfo;

		if (!string.IsNullOrWhiteSpace(outputInfo.GeneratedFilesOutputDirectory))
		{
			string generated = projectDirectory is null
				? outputInfo.GeneratedFilesOutputDirectory
				: System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDirectory, outputInfo.GeneratedFilesOutputDirectory));
			yield return System.IO.Path.Combine(generated, GeneratorAssemblyFolder);
		}

		if (outputInfo.AssemblyPath is string assemblyPath && System.IO.Path.GetDirectoryName(assemblyPath) is { Length: > 0 } intermediate)
		{
			yield return System.IO.Path.Combine(intermediate, "generated", GeneratorAssemblyFolder);
			yield break;
		}

		// No intermediate path known: assume the conventional bin/{config}/{tfm} → obj/{config}/{tfm} layout.
		if (projectDirectory is null || project.OutputFilePath is not string outputFilePath || System.IO.Path.GetDirectoryName(outputFilePath) is not string outputDirectory)
			yield break;

		string[] parts = System.IO.Path.GetRelativePath(projectDirectory, outputDirectory)
			.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length >= 2 && string.Equals(parts[0], "bin", StringComparison.OrdinalIgnoreCase))
			yield return System.IO.Path.Combine([projectDirectory, "obj", .. parts[1..], "generated", GeneratorAssemblyFolder]);
	}

	/// <summary>
	/// Classifies the snapshot in <paramref name="generatedDirectory"/> against the .razor/.cshtml sources it was
	/// generated from and the inputs the generator read.
	/// <para>
	/// Matching runs source→hint-name, never the reverse: the generator flattens paths into hint names with
	/// underscores, which cannot be un-flattened unambiguously (Views/Shared/_Layout.cshtml and a literal
	/// Views_Shared__Layout.cshtml collide). Computing the expected hint name for each known source is
	/// deterministic, so a .g.cs is an orphan exactly when no source claims it.
	/// </para>
	/// <para>
	/// Directive files (_Imports.razor, _ViewImports.cshtml, _ViewStart.cshtml) claim their own output when the
	/// SDK emitted one (every SDK checked does: _Imports_razor.g.cs and so on) but are never required to have
	/// one; an edit to one changes every component's generated code, so it must not be newer than the snapshot.
	/// </para>
	/// <para>
	/// Generated code also depends on the C# the generator discovers components and their parameters from: a
	/// code-behind file, or a component in a referenced project. So no compile input of the project or of the
	/// projects it references may be newer than the oldest generated file either, or a rename made after the
	/// build would survive in the snapshot as a phantom error.
	/// </para>
	/// </summary>
	public static Analysis Analyze(Solution solution, Project project, string generatedDirectory)
	{
		var files = new List<(string Path, string HintName)>();
		bool fresh = true;

		try
		{
			string projectDir = project.FilePath is string projectFilePath
				? System.IO.Path.GetDirectoryName(projectFilePath)!
				: string.Empty;
			if (projectDir.Length == 0)
				return new Analysis(false, files);

			var sourcesByHintKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var componentSources = new List<string>();
			var directiveSources = new List<string>();

			foreach (string source in EnumerateRazorSources(project, projectDir))
			{
				(IsDirectiveOnly(source) ? directiveSources : componentSources).Add(source);
				string relative = System.IO.Path.GetRelativePath(projectDir, source);

				// Folder-preserved layout: relative folders survive, only the file name is flattened.
				string relativeDir = System.IO.Path.GetDirectoryName(relative) ?? "";
				sourcesByHintKey[string.Concat(relativeDir, "|", FlattenToHintName(System.IO.Path.GetFileName(relative)))] = source;

				// Flat layout (older SDKs): the whole relative path is flattened into the file name.
				sourcesByHintKey[string.Concat("|", FlattenToHintName(relative))] = source;
			}

			var matchedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			DateTime oldestGenerated = DateTime.MaxValue;

			foreach (string file in Directory.EnumerateFiles(generatedDirectory, "*.g.cs", SearchOption.AllDirectories))
			{
				// The generator emits under a folder named after itself
				// (Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator); the project-relative
				// structure starts beneath it.
				string[] segments = System.IO.Path.GetRelativePath(generatedDirectory, file)
					.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
				if (segments.Length > 1 && segments[0].StartsWith("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase))
					segments = segments[1..];

				string hintName = string.Join('/', segments);
				string fileName = segments[^1];
				if (!fileName.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase) &&
					!fileName.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase))
				{
					// Not a razor/cshtml output; keep it without letting it decide snapshot validity.
					files.Add((file, hintName));
					continue;
				}

				string? source = sourcesByHintKey.GetValueOrDefault(
					string.Concat(System.IO.Path.Combine(segments[..^1]), "|", fileName));
				if (source is null && segments.Length == 1)
					source = sourcesByHintKey.GetValueOrDefault(string.Concat("|", fileName));

				if (source is null)
				{
					fresh = false;
					continue;
				}

				files.Add((file, hintName));
				matchedSources.Add(source);

				DateTime generatedAt = File.GetLastWriteTimeUtc(file);
				if (generatedAt < oldestGenerated)
					oldestGenerated = generatedAt;

				// Stale: the source was edited after the last build emitted this file.
				if (File.GetLastWriteTimeUtc(source) > generatedAt)
					fresh = false;
			}

			// Incomplete: a component added since the last build has no .g.cs, so its partial would be missing.
			if (componentSources.Any(source => !matchedSources.Contains(source)))
				fresh = false;

			// A directive file edited after the snapshot invalidates every emitted file at once.
			if (directiveSources.Any(directive => File.GetLastWriteTimeUtc(directive) > oldestGenerated))
				fresh = false;

			if (fresh && oldestGenerated != DateTime.MaxValue && NewestCompileInput(solution, project) > oldestGenerated)
				fresh = false;
		}
		catch (Exception)
		{
			fresh = false;
		}

		return new Analysis(fresh, files);
	}

	/// <summary>
	/// The newest write time among the sources, project files and configuration the generator's view of
	/// <paramref name="project"/> derives from, including every project it references. Build artifacts (the
	/// intermediate directory's GlobalUsings.g.cs, AssemblyInfo.cs and generated editorconfig, rewritten by
	/// design-time builds) are skipped: they derive from the project files, which are checked.
	/// </summary>
	private static DateTime NewestCompileInput(Solution solution, Project project)
	{
		DateTime newest = DateTime.MinValue;
		var visited = new HashSet<ProjectId>();
		var pending = new Stack<ProjectId>();
		pending.Push(project.Id);

		while (pending.TryPop(out ProjectId? projectId))
		{
			if (!visited.Add(projectId) || solution.GetProject(projectId) is not Project current)
				continue;

			Consider(current.FilePath, current);
			foreach (Document document in current.Documents)
			{
				if (!RazorMapping.IsRazorGeneratedDocument(document))
					Consider(document.FilePath, current);
			}

			foreach (TextDocument document in current.AdditionalDocuments)
				Consider(document.FilePath, current);
			foreach (TextDocument document in current.AnalyzerConfigDocuments)
				Consider(document.FilePath, current);
			foreach (ProjectReference reference in current.ProjectReferences)
				pending.Push(reference.ProjectId);
		}

		return newest;

		void Consider(string? path, Project owner)
		{
			if (path is null || IsBuildArtifact(path, owner) || !File.Exists(path))
				return;

			DateTime writtenAt = File.GetLastWriteTimeUtc(path);
			if (writtenAt > newest)
				newest = writtenAt;
		}
	}

	private static bool IsBuildArtifact(string path, Project project)
	{
		if (project.CompilationOutputInfo.AssemblyPath is string assemblyPath
			&& System.IO.Path.GetDirectoryName(assemblyPath) is { Length: > 0 } intermediate
			&& IsUnder(path, intermediate))
		{
			return true;
		}

		if (project.FilePath is not string projectFilePath || System.IO.Path.GetDirectoryName(projectFilePath) is not string projectDir || !IsUnder(path, projectDir))
			return false;

		string firstSegment = System.IO.Path.GetRelativePath(projectDir, path)
			.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
		return string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsUnder(string path, string directory) =>
		path.StartsWith(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

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
	/// included (linked files outside the project directory, exclusions). A disk scan is always
	/// merged in so files present on disk but not yet in the design-time graph (e.g. a newly written
	/// <c>_Imports.razor</c>) still participate in snapshot freshness.
	/// </summary>
	private static IEnumerable<string> EnumerateRazorSources(Project project, string projectDir)
	{
		HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);

		foreach (string path in project.AdditionalDocuments
			.Select(document => document.FilePath)
			.OfType<string>()
			.Where(RazorDocumentGenerator.IsRazorSourcePath))
		{
			paths.Add(path);
		}

		try
		{
			foreach (string file in Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories)
				.Concat(Directory.EnumerateFiles(projectDir, "*.cshtml", SearchOption.AllDirectories)))
			{
				// Build artifacts under bin/obj are not project sources.
				string relative = System.IO.Path.GetRelativePath(projectDir, file);
				string firstSegment = relative.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
				if (string.Equals(firstSegment, "bin", StringComparison.OrdinalIgnoreCase)
					|| string.Equals(firstSegment, "obj", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				paths.Add(file);
			}
		}
		catch
		{
			// Disk scan is best-effort; additional documents alone still apply.
		}

		return paths;
	}
}
