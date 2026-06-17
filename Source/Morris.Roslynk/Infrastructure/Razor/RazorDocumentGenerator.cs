using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
		if (!HasRazorInputs(project) || !TryGetGenerator(project, out IIncrementalGenerator? generator))
			return solution;

		try
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is null || project.ParseOptions is not CSharpParseOptions parseOptions)
				return solution;

			GeneratorDriver driver = CSharpGeneratorDriver.Create(
				generators: [generator!.AsSourceGenerator()],
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
			// Freshness, not correctness: a generator that throws leaves the project as the workspace loaded it.
			return solution;
		}
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

		if (reference?.FullPath is not string razorPath)
			return false;

		generator = Generators.GetOrAdd(razorPath, Load);
		return generator is not null;
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
