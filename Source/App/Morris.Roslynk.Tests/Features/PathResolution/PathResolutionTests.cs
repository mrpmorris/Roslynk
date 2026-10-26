using Morris.Roslynk.Features.CodeActions.GetCodeActions;
using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Features.Patching.ApplyPatch;
using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Features.Symbols.GetExpressionInfo;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.PathResolution;

public class PathResolutionTests
{
	[Fact]
	public async Task WhenGetDiagnosticsIsFilteredByABareFileName_ThenItResolvesAndEchoesResolvedPath()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		Directory.CreateDirectory(Path.Combine(projectDirectory, "Sub"));
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Sub", "Broken.cs"),
			"namespace SimpleLibrary;\n\npublic class Broken\n{\n\tpublic int Value() => \"text\";\n}\n");
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "Broken.cs");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("filter=filePath:Broken.cs", result);
		Assert.Contains("resolvedPath=SimpleLibrary/Sub/Broken.cs", result);
		Assert.Contains("errors=1", result);
		Assert.Contains("CS0029,", result);
	}

	[Fact]
	public async Task WhenGetDiagnosticsIsFilteredByABareFileNameMatchingSeveralFiles_ThenAmbiguousOffersCandidates()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		Directory.CreateDirectory(Path.Combine(projectDirectory, "Other"));
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Other", "Widget.cs"),
			"namespace SimpleLibrary;\n\npublic class OtherWidget\n{\n}\n");
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "Widget.cs");

		Assert.Contains("error=Ambiguous", result);
		Assert.Contains("candidate=SimpleLibrary/Widget.cs", result);
		Assert.Contains("candidate=SimpleLibrary/Other/Widget.cs", result);
		Assert.DoesNotContain("resolvedPath=", result);
	}

	[Fact]
	public async Task WhenGetDiagnosticsUsesAnExactPath_ThenNoResolvedPathHeaderIsEmitted()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Simple, includeErrors: true, includeAnalyzers: false, filePath: "SimpleLibrary/Calculator.cs");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("resolvedPath=", result);
		Assert.Contains("filter=filePath:SimpleLibrary/Calculator.cs", result);
	}
	[Fact]
	public async Task WhenApplyPatchUsesABareFileName_ThenItResolvesAndEchoesResolvedPath()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);

		string calculator = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "Calculator.cs");
		string original = await File.ReadAllTextAsync(calculator);
		string body = original.TrimEnd('\r', '\n');
		string patch = BuildFullReplacePatch("Calculator.cs", body, body + "\n// patched");

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.True(result.Contains("applied=Y", StringComparison.Ordinal), result);
		Assert.Contains("resolvedPath=SimpleLibrary/Calculator.cs", result);
		Assert.Contains("// patched", await File.ReadAllTextAsync(calculator));
	}

	[Fact]
	public async Task WhenGetCodeActionsUsesABareFileName_ThenItResolvesAndEchoesResolvedPath()
	{
		string solutionPath = Helpers.UnnecessaryUsingScenario.Create(out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetCodeActionsTool(registry, TestServices.CodeActions());

		string result = await subject.GetCodeActions(
			solutionPath, "Greeter.cs", Helpers.UnnecessaryUsingScenario.UsingLine, 1);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedPath=CodeStyleLibrary/Greeter.cs", result);
		Assert.Contains(",IDE0005 ", result);
	}

	[Fact]
	public async Task WhenFindDefinitionUsesABareFileName_ThenItResolvesAndEchoesResolvedPath()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new FindDefinitionTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.FindDefinition(TestSolutions.Simple, "Calculator.cs", 7, 10);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedPath=SimpleLibrary/Calculator.cs", result);
		Assert.Contains("fullName=a", result);
	}
	[Fact]
	public async Task WhenGetExpressionInfoUsesABareFileName_ThenItResolvesAndEchoesResolvedPath()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
		var subject = new GetExpressionInfoTool(registry, new ProjectionService());

		string result = await subject.GetExpressionInfo(TestSolutions.Simple, "Calculator.cs", 7, 10);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("resolvedPath=SimpleLibrary/Calculator.cs", result);
		Assert.Contains("expr=a", result);
	}

	private static string BuildFullReplacePatch(string relativePath, string originalText, string newText)
	{
		List<string> oldLines = SplitWithoutEol(originalText);
		List<string> newLines = SplitWithoutEol(newText);

		var builder = new System.Text.StringBuilder();
		builder.Append($"--- a/{relativePath}\n");
		builder.Append($"+++ b/{relativePath}\n");
		builder.Append($"@@ -1,{oldLines.Count} +1,{newLines.Count} @@\n");
		foreach (string line in oldLines)
			builder.Append('-').Append(line).Append('\n');
		foreach (string line in newLines)
			builder.Append('+').Append(line).Append('\n');

		return builder.ToString();
	}

	private static List<string> SplitWithoutEol(string text) =>
		[.. text.Replace("\r\n", "\n").Split('\n')];
}
