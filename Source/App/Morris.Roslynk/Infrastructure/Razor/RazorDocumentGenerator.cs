using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

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
/// </summary>
public static class RazorDocumentGenerator
{
	private const string RazorCompilerFileName = "Microsoft.CodeAnalysis.Razor.Compiler.dll";
	private const string GeneratorTypeName = "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator";
	private const string GeneratedFolder = "RoslynkRazorGenerated";

	private static readonly ConcurrentDictionary<string, IIncrementalGenerator?> Generators = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Adds the generated Razor documents for every Razor project in the solution, returning the augmented solution.</summary>
	public static async Task<Solution> AugmentAsync(Solution solution, CancellationToken cancellationToken = default)
	{
		if (solution is null)
			throw new ArgumentNullException(nameof(solution));

		foreach (ProjectId projectId in solution.ProjectIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (solution.GetProject(projectId) is Project project)
				solution = await AugmentProjectAsync(solution, project, cancellationToken);
		}

		return solution;
	}

	private static async Task<Solution> AugmentProjectAsync(Solution solution, Project project, CancellationToken cancellationToken)
	{
		bool hasGenerator = TryGetGenerator(project, out IIncrementalGenerator? generator);
		bool hasRazorInputs = HasRazorInputs(project);

		if (hasGenerator && hasRazorInputs)
			solution = await RunGeneratorAsync(solution, project, generator!, cancellationToken);

		solution = await AddPreGeneratedFilesAsync(solution, project, cancellationToken);

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

	private static async Task<Solution> AddPreGeneratedFilesAsync(Solution solution, Project project, CancellationToken cancellationToken)
	{
		if (project.FilePath is not string projectFilePath || project.OutputFilePath is not string outputFilePath)
			return solution;

		string? generatedDir = GetRazorGeneratedDirectory(projectFilePath, outputFilePath);
		if (generatedDir is null || !Directory.Exists(generatedDir))
			return solution;

		try
		{
			foreach (string file in Directory.EnumerateFiles(generatedDir, "*.g.cs", SearchOption.AllDirectories))
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

	private static string? GetRazorGeneratedDirectory(string projectFilePath, string outputFilePath)
	{
		string projectDir = System.IO.Path.GetDirectoryName(projectFilePath)!;
		string? outputDir = System.IO.Path.GetDirectoryName(outputFilePath);

		if (outputDir is null)
			return null;

		string relativeOutput = System.IO.Path.GetRelativePath(projectDir, outputDir);
		string[] parts = relativeOutput.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);

		if (parts.Length < 2 || !string.Equals(parts[0], "bin", StringComparison.OrdinalIgnoreCase))
			return null;

		string configuration = parts[1];
		string targetFramework = parts.Length > 2 ? parts[2] : "";

		return System.IO.Path.Combine(projectDir, "obj", configuration, targetFramework, "generated", "Microsoft.CodeAnalysis.Razor.Compiler");
	}

	private static bool HasRazorInputs(Project project) =>
		project.AdditionalDocuments.Any(document =>
			document.FilePath is string path
			&& (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)));

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
