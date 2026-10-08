using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Finds the Razor output a previous <c>dotnet build</c> left on disk (<c>EmitCompilerGeneratedFiles=true</c>), the
/// fallback when no Razor generator can be loaded. That output is a build artifact MSBuild never prunes: a
/// <c>.g.cs</c> can outlive its deleted <c>.razor</c> source (an orphan), which would poison the compilation with
/// phantom CS0103/CS0246 for symbols that no longer exist anywhere, so orphans are dropped.
/// </summary>
internal static class RazorSnapshot
{
	private const string GeneratorAssemblyFolder = "Microsoft.CodeAnalysis.Razor.Compiler";

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
	/// The snapshot's <c>.g.cs</c> files in <paramref name="generatedDirectory"/> whose .razor/.cshtml source still
	/// exists, each with the hint name the generator gave it (its path below the generator's folder); output that
	/// is not Razor's is kept as is.
	/// <para>
	/// Matching runs source→hint-name, never the reverse: the generator flattens paths into hint names with
	/// underscores, which cannot be un-flattened unambiguously (Views/Shared/_Layout.cshtml and a literal
	/// Views_Shared__Layout.cshtml collide). Computing the expected hint name for each known source is
	/// deterministic, so a .g.cs is an orphan exactly when no source claims it.
	/// </para>
	/// <para>
	/// Directive files (_Imports.razor, _ViewImports.cshtml, _ViewStart.cshtml) claim their own output like any
	/// other source (every SDK checked emits one: _Imports_razor.g.cs and so on).
	/// </para>
	/// </summary>
	public static IReadOnlyList<(string Path, string HintName)> Files(Project project, string generatedDirectory)
	{
		var files = new List<(string Path, string HintName)>();

		try
		{
			string projectDir = project.FilePath is string projectFilePath
				? System.IO.Path.GetDirectoryName(projectFilePath)!
				: string.Empty;
			if (projectDir.Length == 0)
				return files;

			var sourcesByHintKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (string source in EnumerateRazorSources(project, projectDir))
			{
				string relative = System.IO.Path.GetRelativePath(projectDir, source);

				// Folder-preserved layout: relative folders survive, only the file name is flattened.
				string relativeDir = System.IO.Path.GetDirectoryName(relative) ?? "";
				sourcesByHintKey[string.Concat(relativeDir, "|", FlattenToHintName(System.IO.Path.GetFileName(relative)))] = source;

				// Flat layout (older SDKs): the whole relative path is flattened into the file name.
				sourcesByHintKey[string.Concat("|", FlattenToHintName(relative))] = source;
			}

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
					// Not a razor/cshtml output; keep it.
					files.Add((file, hintName));
					continue;
				}

				string? source = sourcesByHintKey.GetValueOrDefault(
					string.Concat(System.IO.Path.Combine(segments[..^1]), "|", fileName));
				if (source is null && segments.Length == 1)
					source = sourcesByHintKey.GetValueOrDefault(string.Concat("|", fileName));

				// An orphan: its source was deleted after the last build.
				if (source is not null)
					files.Add((file, hintName));
			}
		}
		catch (Exception)
		{
			// A snapshot that cannot be read in full is not used at all.
			return [];
		}

		return files;
	}

	/// <summary>
	/// The hint name the razor generator derives from a path: every character that is not a letter or
	/// digit becomes an underscore, and <c>.g.cs</c> is appended (Pages/_Host.cshtml → Pages__Host_cshtml.g.cs).
	/// </summary>
	private static string FlattenToHintName(string path) =>
		string.Concat(path.Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".g.cs";

	/// <summary>
	/// Enumerates the project's .razor/.cshtml files. The workspace's additional documents are
	/// included (linked files outside the project directory, exclusions). A disk scan is always
	/// merged in so files present on disk but not yet in the design-time graph (e.g. a newly written
	/// <c>_Imports.razor</c>) still claim their output.
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
