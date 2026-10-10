using Morris.Roslynk.Features.Diagnostics.GetDiagnostics;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.Diagnostics.GetDiagnosticsTests;

public class GetDiagnosticsTests
{
	[Fact]
	public async Task WhenASolutionHasACompileError_ThenItIsReturnedAsAnError()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken, includeErrors: true);

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("errors=0", result);
		Assert.Contains(result.Split('\n'), line => line == "BrokenLibrary");
		Assert.Contains("\terrors\n", result);
		Assert.Contains("CS0029,9:27,", result);
	}

	[Fact]
	public async Task WhenSeveritiesAreNotWidened_ThenOnlyErrorsAreReturnedButTheCountsStillSeeWarnings()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken, includeErrors: true);

		// Counts are always in the header; body only shows errors when includeErrors is set.
		Assert.Contains("warnings=0", result);
		Assert.DoesNotContain("\twarnings", result);
	}

	[Fact]
	public async Task WhenWarningsAreIncluded_ThenTheyAppearAlongsideErrors()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeWarnings: true);

		// The fixture has zero warnings, so only errors appear in the body.
		Assert.Contains("warnings=0", result);
		Assert.Contains("\terrors\n", result);
	}

	[Fact]
	public async Task WhenARazorFileHasARazorSyntaxError_ThenTheRazorDiagnosticIsReportedAgainstThatFile()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string broken = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib", "Broken.razor");
		await File.WriteAllTextAsync(broken, "<h1>Broken</h1>\n@code {\n    private string Value = \"\";\n");
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false);

		string[] lines = result.Split('\n');
		int file = Array.FindIndex(lines, line => line.Trim() == "Broken.razor");
		Assert.True(file >= 0, result);
		Assert.Contains(lines.Skip(file), line => line.TrimStart('\t').StartsWith("RZ1006,2:", StringComparison.Ordinal));
		Assert.Contains(lines, line => line == "RazorLib");
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken);

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Broken);
	}

	[Fact]
	public async Task WhenFilteredByFile_ThenOnlyThatFilesDiagnosticsAreReturned()
	{
		string solutionPath = await CreateScratchWithBrokenFilesAsync(conversionErrors: 1);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string unfiltered = await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false);
		Assert.Contains("CS0029,", unfiltered);
		Assert.Contains("CS0103,", unfiltered);

		string result = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "SimpleLibrary/Broken.cs");

		Assert.Contains("errors=1", result);
		Assert.Contains("filter=filePath:SimpleLibrary/Broken.cs", result);
		Assert.Contains("CS0029,", result);
		Assert.DoesNotContain("CS0103,", result);
		Assert.DoesNotContain("AlsoBroken", result);
	}

	[Fact]
	public async Task WhenFilteredByFileWithAbsolutePath_ThenRelativeAndAbsoluteMatch()
	{
		string solutionPath = await CreateScratchWithBrokenFilesAsync(conversionErrors: 1);
		string absolutePath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary", "Broken.cs");
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string relative = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "SimpleLibrary/Broken.cs");
		string absolute = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: absolutePath);

		Assert.Contains($"filter=filePath:{absolutePath}", absolute);
		Assert.Contains("CS0029,", absolute);
		Assert.DoesNotContain("CS0103,", absolute);
		Assert.Equal(BodyOf(relative), BodyOf(absolute));
	}

	[Fact]
	public async Task WhenFilteredByIds_ThenOnlyThoseIdsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeAnalyzers: false, ids: ["CS0029"]);

		Assert.Contains("errors=1", result);
		Assert.Contains("warnings=0", result);
		Assert.Contains("filter=ids:CS0029", result);
		Assert.Contains("CS0029,9:27,", result);
		Assert.DoesNotContain("CS0169,", result);
	}

	[Fact]
	public async Task WhenIdsDifferInCase_ThenTheyStillMatch()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeAnalyzers: false, ids: ["cs0029"]);

		Assert.Contains("errors=1", result);
		Assert.Contains("filter=ids:cs0029", result);
		Assert.Contains("CS0029,9:27,", result);
		Assert.DoesNotContain("CS0169,", result);
	}

	[Fact]
	public async Task WhenMaxResultsIsExceeded_ThenTheListIsTruncatedAndCountsStayComplete()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeWarnings: true, includeAnalyzers: false, maxResults: 1);

		Assert.Contains("errors=2", result);
		Assert.Contains("warnings=0", result);
		Assert.Contains("count=2", result);
		Assert.Contains("truncated=Y", result);
		string[] leaves = [.. result.Split('\n').Where(line => line.StartsWith('\t') && line.Contains("CS0"))];
		Assert.Single(leaves);
		Assert.StartsWith("CS0169,", leaves[0].TrimStart('\t'));
	}

	[Fact]
	public async Task WhenSummaryOnly_ThenDiagnosticsAreGroupedById()
	{
		string solutionPath = await CreateScratchWithBrokenFilesAsync(conversionErrors: 2);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(solutionPath, includeAnalyzers: false, summaryOnly: true);

		Assert.Contains("errors=3", result);
		Assert.Contains("\nCS0029,2,SimpleLibrary,SimpleLibrary/Broken.cs,", result);
		Assert.Contains("\nCS0103,1,SimpleLibrary,SimpleLibrary/AlsoBroken.cs,", result);
		Assert.DoesNotContain("CS0029,1,", result);
		Assert.True(
			result.IndexOf("\nCS0029,2,", StringComparison.Ordinal) < result.IndexOf("\nCS0103,1,", StringComparison.Ordinal),
			result);
	}

	[Fact]
	public async Task WhenFilteredByProjectName_ThenEveryTargetFrameworkIsIncluded()
	{
		// The scratch copy keeps both target frameworks: under Source/, Directory.Build.props forces the
		// committed fixture to a single net10.0 target framework.
		string solutionPath = TestSolutions.CreateScratchMultiTargetSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, projectName: "Multi");

		// Legacy.cs fails to bind in net8.0 (two CS0246 'Int32' parameters plus two CS1503 call sites) on
		// top of Api.cs's CS0246 'Type', which is reported against netstandard2.0 only.
		Assert.Contains("errors=5", result);
		Assert.Contains("filter=projectName:Multi", result);
		// The Api.cs diagnostic is the every-target-framework proof: it exists only in the netstandard2.0
		// framework, so a filter that dropped later target frameworks would not list it.
		Assert.Contains("Api.cs", result);
		Assert.Contains("CS0246,9:25,The type or namespace name 'Type'", result);
	}

	[Fact]
	public async Task WhenProjectNameCarriesTheExtensionOrCase_ThenItStillMatches()
	{
		string solutionPath = TestSolutions.CreateScratchMultiTargetSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string withExtension = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, projectName: "Multi.csproj");
		string lowerCase = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, projectName: "multi");

		foreach (string result in new[] { withExtension, lowerCase })
		{
			Assert.Contains("filter=projectName:Multi", result);
			Assert.Contains("CS0246,", result);
		}
	}

	[Fact]
	public async Task WhenProjectNameMatchesNothing_ThenNotFoundOffersCandidates()
	{
		string solutionPath = TestSolutions.CreateScratchMultiTargetSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, projectName: "Nope");

		Assert.Contains("error=NotFound", result);
		Assert.Contains("candidate=Multi", result);

		// The refusal did not poison the instance: a well-named call still works.
		string retry = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, projectName: "Multi");
		Assert.Contains("CS0246,", retry);
	}

	[Fact]
	public async Task WhenFilePathMatchesNoDocument_ThenNotFoundOffersCandidates()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeAnalyzers: false, filePath: "Nope/Missing.cs");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenACompiledFileHasNoMatchingDiagnostics_ThenTheCountsAreZeroAndNotAnError()
	{
		string solutionPath = await CreateScratchWithBrokenFilesAsync(conversionErrors: 1);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "SimpleLibrary/Calculator.cs");

		Assert.Contains("errors=0", result);
		Assert.Contains("filter=filePath:SimpleLibrary/Calculator.cs", result);
		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("count=", result);
	}

	[Fact]
	public async Task WhenMaxResultsIsNegative_ThenTheResultIsHeadersOnly()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken, includeErrors: true, includeAnalyzers: false, maxResults: -5);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("errors=2", result);
		Assert.Contains("count=2", result);
		Assert.Contains("truncated=Y", result);
		Assert.DoesNotContain("CS0029", result);
	}

	[Fact]
	public async Task WhenFiltersCombine_ThenEveryActiveFilterIsEchoedAndCountsAgree()
	{
		string solutionPath = await CreateScratchWithBrokenFilesAsync(conversionErrors: 1);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath,
			includeErrors: true,
			includeAnalyzers: false,
			projectName: "SimpleLibrary",
			filePath: "SimpleLibrary/Broken.cs",
			ids: ["CS0029"]);

		Assert.Contains("filter=projectName:SimpleLibrary", result);
		Assert.Contains("filter=filePath:SimpleLibrary/Broken.cs", result);
		Assert.Contains("filter=ids:CS0029", result);
		Assert.Contains("errors=1", result);
		Assert.Contains("CS0029,", result);
		Assert.DoesNotContain("CS0103,", result);
	}

	[Fact]
	public async Task WhenFilteredByARazorFile_ThenRazorAndMappedDiagnosticsAreReturned()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string razorLib = Path.Combine(Path.GetDirectoryName(solutionPath)!, "RazorLib");
		await File.WriteAllTextAsync(
			Path.Combine(razorLib, "Broken.razor"), "<h1>Broken</h1>\n@code {\n    private string Value = \"\";\n");
		await File.WriteAllTextAsync(
			Path.Combine(razorLib, "Mapped.razor"), "<p>Mapped</p>\n@code {\n    public int Bad() => \"no\";\n}\n");
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string broken = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "RazorLib/Broken.razor");

		Assert.Contains("filter=filePath:RazorLib/Broken.razor", broken);
		Assert.Contains("RZ1006,", broken);
		Assert.DoesNotContain("CS0029,", broken);

		string mapped = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, filePath: "RazorLib/Mapped.razor");

		Assert.Contains("filter=filePath:RazorLib/Mapped.razor", mapped);
		Assert.Contains("CS0029,", mapped);
		Assert.DoesNotContain("RZ1006,", mapped);
	}

	[Fact]
	public async Task WhenNoFiltersAreSupplied_ThenTheOutputIsUnchanged()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken, includeErrors: true, includeAnalyzers: false);

		Assert.DoesNotContain("filter=", result);
		Assert.DoesNotContain("count=", result);
		Assert.DoesNotContain("truncated=", result);
		Assert.Contains("CS0029,9:27,", result);
	}

	[Fact]
	public async Task WhenTruncated_ThenTheKeptLeavesAreTheFirstInBodyOrder()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		Directory.CreateDirectory(Path.Combine(projectDirectory, "Sub"));
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Aaa.cs"),
			"namespace SimpleLibrary;\n\npublic class Aaa\n{\n\tpublic int Value() => \"text\";\n}\n");
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Sub", "Zzz.cs"),
			"namespace SimpleLibrary;\n\npublic class Zzz\n{\n\tpublic int Value() => \"text\";\n}\n");
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string full = await subject.GetDiagnostics(solutionPath, includeErrors: true, includeAnalyzers: false);
		string truncated = await subject.GetDiagnostics(
			solutionPath, includeErrors: true, includeAnalyzers: false, maxResults: 1);

		string[] fullLeaves = [.. BodyOf(full).Split('\n').Where(line => line.Contains("CS0"))];
		string[] keptLeaves = [.. BodyOf(truncated).Split('\n').Where(line => line.Contains("CS0"))];

		Assert.True(fullLeaves.Length > 1, full);
		Assert.Single(keptLeaves);
		Assert.Equal(fullLeaves[0], keptLeaves[0]);
		Assert.Contains("Aaa.cs", full[..full.IndexOf(fullLeaves[0], StringComparison.Ordinal)]);
	}

	[Fact]
	public async Task WhenSummaryOnlyCoversEverySeverityWithoutToggles_ThenTheHeaderCountsStayComplete()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(TestSolutions.Broken, includeAnalyzers: false, summaryOnly: true);

		// No include flag is set, yet the summary still lists every id - unlike detail mode, whose body would
		// be empty. The header counts keep their all-severities scope.
		Assert.Contains("errors=2", result);
		Assert.Contains("warnings=0", result);
		Assert.Contains("\nCS0029,1,BrokenLibrary,BrokenLibrary/Broken.cs,9:27,", result);
		Assert.Contains("\nCS0169,1,BrokenLibrary,BrokenLibrary/Broken.cs,", result);
		Assert.DoesNotContain("count=", result);
	}

	[Fact]
	public async Task WhenSummaryOnlyIsCapped_ThenCountCountsIdRows()
	{
		string solutionPath = await CreateScratchWithBrokenFilesAsync(conversionErrors: 2);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			solutionPath, includeAnalyzers: false, summaryOnly: true, ids: ["CS0029", "CS0103"], maxResults: 1);

		// Three diagnostics collapse to two id rows; the cap counts id rows, not diagnostics.
		Assert.Contains("count=2", result);
		Assert.Contains("truncated=Y", result);
		Assert.Contains("errors=3", result);
		string[] body = BodyOf(result).Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Single(body);
		Assert.StartsWith("CS0029,2,", body[0]);
	}

	[Fact]
	public async Task WhenFilteredByAPrivateFixTriggerId_ThenNothingMatches()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Broken);
		var subject = new GetDiagnosticsTool(registry, new DiagnosticsService());

		string result = await subject.GetDiagnostics(
			TestSolutions.Broken,
			includeErrors: true,
			includeAnalyzers: false,
			ids: ["RemoveUnnecessaryImportsFixable"]);

		Assert.DoesNotContain("error=", result);
		Assert.Contains("errors=0", result);
		Assert.Contains("warnings=0", result);
		Assert.Contains("filter=ids:RemoveUnnecessaryImportsFixable", result);
		Assert.DoesNotContain("CS0029", result);
	}

	private static string BodyOf(string result)
	{
		int blank = result.IndexOf("\n\n", StringComparison.Ordinal);
		Assert.True(blank >= 0, result);
		return result[(blank + 2)..];
	}

	/// <summary>
	/// A scratch SimpleSolution whose project carries <paramref name="conversionErrors"/> CS0029s in
	/// Broken.cs and one CS0103 in AlsoBroken.cs, so file/id/count tests have two files to filter between.
	/// </summary>
	private static async Task<string> CreateScratchWithBrokenFilesAsync(int conversionErrors)
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");

		string methods = string.Join("\n", Enumerable.Range(0, conversionErrors)
			.Select(index => $"\tpublic int Value{index}() => \"text\";"));
		string broken =
			"namespace SimpleLibrary;\n"
			+ "\n"
			+ "public class Broken\n"
			+ "{\n"
			+ methods
			+ "\n}\n";
		string alsoBroken =
			"namespace SimpleLibrary;\n"
			+ "\n"
			+ "public class AlsoBroken\n"
			+ "{\n"
			+ "\tpublic int Sum() => Missing + 1;\n"
			+ "}\n";

		await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Broken.cs"), broken);
		await File.WriteAllTextAsync(Path.Combine(projectDirectory, "AlsoBroken.cs"), alsoBroken);
		return solutionPath;
	}
}
