using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Features.Diagnostics.GeneratorDiagnosticsTests;

public class GeneratorDiagnosticsTests
{
	[Fact]
	public async Task WhenAProjectReferencesASourceGenerator_ThenGeneratedTypesAreInTheCompilation()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Generator);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Generator);

		// The consumer compiles only if HelloGenerator's output is part of the compilation.
		Assert.Contains("errors=0", result);
		Assert.DoesNotContain("CS0246", result);
	}

	[Fact]
	public async Task WhenTheGeneratorAssemblyCannotBeLoaded_ThenTheLoadReportsTheFailure()
	{
		// A scratch copy excludes bin/obj; plant a corrupt DLL at the generator's output path so the
		// analyzer reference resolves to a file that cannot possibly load.
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		string corruptDll = Path.Combine(
			Path.GetDirectoryName(solutionPath)!, "GeneratorLib", "bin", "Debug", "netstandard2.0", "GeneratorLib.dll");
		Directory.CreateDirectory(Path.GetDirectoryName(corruptDll)!);
		File.WriteAllText(corruptDll, "not a PE image");

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		// The generator cannot run, so the unloadable reference must be called out rather than the
		// compilation silently degrading to phantom CS0246s.
		Assert.Contains(workspace.LoadDiagnostics, d =>
			d.Contains("Analyzer load failed", StringComparison.Ordinal) &&
			d.Contains("GeneratorLib.dll", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenTheGeneratorDllIsMissing_ThenTheLoadDiagnosticNamesTheConsumerTheGeneratorProjectAndThePath()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();

		using SolutionWorkspace workspace = await SolutionWorkspace.LoadAsync(solutionPath);

		string message = Assert.Single(workspace.LoadDiagnostics, d => d.Contains("Skipped unresolved analyzer", StringComparison.Ordinal));
		Assert.Contains("'ConsumerLib'", message, StringComparison.Ordinal);
		Assert.Contains(Path.Combine("GeneratorLib", "bin", "Debug", "netstandard2.0", "GeneratorLib.dll"), message, StringComparison.OrdinalIgnoreCase);
		Assert.Contains("Build project 'GeneratorLib'", message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WhenTheGeneratorIsBuiltAfterLoad_ThenTheNextCallCompilesItsGeneratedCodeWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		SolutionWorkspace? loaded = instance.Workspace;
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());
		Assert.Contains("errors=2", await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false));

		TestSolutions.BuildScratchGenerator(solutionPath);

		Assert.Contains("errors=0", await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false));
		Assert.Same(loaded, instance.Workspace);
		Assert.DoesNotContain(instance.Workspace!.LoadDiagnostics, d => d.Contains("GeneratorLib.dll", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenTheGeneratorIsRebuiltWithChangedOutput_ThenTheNextCallCompilesTheNewOutputWithoutReload()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		string directory = Path.GetDirectoryName(solutionPath)!;

		// The consumer wants a member the built generator does not emit yet.
		string usesGenerated = Path.Combine(directory, "ConsumerLib", "UsesGenerated.cs");
		await ReplaceInFileAsync(
			usesGenerated,
			"GeneratedNamespace.GreetingCsv.FirstLine;",
			"GeneratedNamespace.GreetingCsv.FirstLine; public static string Farewell() => GeneratedNamespace.Hello.Farewell;");

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		SolutionWorkspace? loaded = instance.Workspace;
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());
		Assert.Contains("CS0117", await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false));

		await ReplaceInFileAsync(
			Path.Combine(directory, "GeneratorLib", "HelloGenerator.cs"),
			"public const string Greeting = \\\"Hello from the generator\\\";",
			"public const string Greeting = \\\"Hello from the generator\\\"; public const string Farewell = \\\"Goodbye\\\";");
		TestSolutions.BuildScratchGenerator(solutionPath);

		Assert.Contains("errors=0", await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false));
		Assert.Same(loaded, instance.Workspace);
	}

	[Fact]
	public async Task WhenTheGeneratorBuildsOutsideEveryProjectDirectory_ThenItIsStillPickedUpWithoutReload()
	{
		// An artifacts-style layout: the DLL lands in a directory no project owns, which the watcher never sees.
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		string directory = Path.GetDirectoryName(solutionPath)!;
		await ReplaceInFileAsync(
			Path.Combine(directory, "GeneratorLib", "GeneratorLib.csproj"),
			"</PropertyGroup>",
			@"<BaseOutputPath>..\artifacts\</BaseOutputPath></PropertyGroup>");

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Assert.Contains(instance.Workspace!.LoadDiagnostics, d =>
			d.Contains(Path.Combine("artifacts", "Debug"), StringComparison.OrdinalIgnoreCase) &&
			d.Contains("Build project 'GeneratorLib'", StringComparison.Ordinal));
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());
		Assert.Contains("errors=2", await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false));

		TestSolutions.BuildScratchGenerator(solutionPath);

		Assert.Contains("errors=0", await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false));
	}

	[Fact]
	public async Task WhenARebuiltGeneratorCannotBeLoaded_ThenTheFailureIsReportedUntilAGoodBuildReplacesIt()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolution();
		string dll = Path.Combine(Path.GetDirectoryName(solutionPath)!, "GeneratorLib", "bin", "Debug", "netstandard2.0", "GeneratorLib.dll");
		byte[] good = await File.ReadAllBytesAsync(dll);
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);
		Assert.DoesNotContain(instance.Workspace!.LoadDiagnostics, d => d.Contains("Analyzer load failed", StringComparison.Ordinal));

		await File.WriteAllTextAsync(dll, "not a PE image");
		await registry.GetOrBeginAsync(solutionPath);
		Assert.Contains(instance.Workspace!.LoadDiagnostics, d =>
			d.Contains("Analyzer load failed", StringComparison.Ordinal) && d.Contains("GeneratorLib.dll", StringComparison.OrdinalIgnoreCase));

		await File.WriteAllBytesAsync(dll, good);
		await registry.GetOrBeginAsync(solutionPath);
		Assert.DoesNotContain(instance.Workspace!.LoadDiagnostics, d => d.Contains("Analyzer load failed", StringComparison.Ordinal));
	}

	private static async Task ReplaceInFileAsync(string path, string oldText, string newText)
	{
		string text = await File.ReadAllTextAsync(path);
		Assert.Contains(oldText, text, StringComparison.Ordinal);
		await File.WriteAllTextAsync(path, text.Replace(oldText, newText, StringComparison.Ordinal));
	}
}
