using Morris.Roslynk.Features.Solutions.GetSolutionStatus;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Watching;

namespace Morris.Roslynk.Tests.Features.Solutions.GetSolutionStatusTests;

public class GetSolutionStatusTests
{
	[Fact]
	public async Task WhenTheFileWatcherReportedAnError_ThenGetSolutionStatusListsIt()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		var watcher = new SolutionFileWatcher(new SolutionFileSync(instance));
		watcher.Diagnostics.Record("The configured user limit (524288) on the number of inotify watches has been reached.", "/src");
		instance.AttachWatcher(() => watcher);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		Assert.Contains("\twatchDiagnostic=The configured user limit (524288)", result);
	}

	[Fact]
	public async Task WhenASolutionIsLoaded_ThenItIsReadyWithLoadedMatchingTotal()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		// One line "<solutionId>,Ready,1/1".
		string line = result.Split('\n').First(candidate => candidate.Contains(",Ready,", StringComparison.Ordinal));
		Assert.EndsWith(",1/1", line);
	}

	[Fact]
	public async Task WhenASolutionIsLoaded_ThenItsSolutionIdUsesForwardSlashes()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		string expected = Path.GetFullPath(TestSolutions.Simple).Replace('\\', '/');
		Assert.Contains($"{expected},Ready,1/1", result);
	}

	[Fact]
	public async Task WhileASolutionIsStillLoading_ThenTheTotalIsUnknownAndStatusIsBuilding()
	{
		using var registry = new InstanceRegistry();
		registry.GetOrBegin(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		Assert.Contains(",Building,", result);
		Assert.Contains("/?", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenTheSolutionHasLoadDiagnostics_ThenGetSolutionStatusListsTheirMessages()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		Assert.Contains("\tloadDiagnostic=", result);
		Assert.Contains("GeneratorLib.dll", result, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task WhenTheSolutionHasNoLoadDiagnostics_ThenGetSolutionStatusStaysOneLinePerSolution()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		Assert.DoesNotContain("loadDiagnostic", result);
		Assert.Single(result.Split('\n', StringSplitOptions.RemoveEmptyEntries));
	}

	[Fact]
	public async Task WhileTheSolutionIsStillLoading_ThenNoLoadDiagnosticsAreListed()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		using var registry = new InstanceRegistry();
		registry.GetOrBegin(solutionPath);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		Assert.Contains(",Building,", result);
		Assert.DoesNotContain("loadDiagnostic", result);

		await registry.GetOrAddAsync(solutionPath);
	}

	[Fact]
	public async Task WhenAllLoadDiagnosticsAreRequested_ThenTheMessagesAreStillListed()
	{
		string solutionPath = TestSolutions.CreateScratchGeneratorSolutionWithoutBuiltGenerator();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus(allLoadDiagnostics: true);

		Assert.Contains("	loadDiagnostic=", result);
		Assert.DoesNotContain("loadDiagnosticsTruncated", result);
	}

	[Fact]
	public void WhenThereAreMoreMessagesThanTheLimit_ThenTheRestAreCountedInTheTruncationLine()
	{
		string[] messages = Enumerable.Range(0, GetSolutionStatusTool.MaxLoadDiagnostics + 5)
			.Select(i => $"message {i:D3}")
			.ToArray();
		var builder = new OutlineBuilder();

		GetSolutionStatusTool.AppendMessages(builder, "loadDiagnostic", messages, GetSolutionStatusTool.MaxLoadDiagnostics);

		string[] lines = builder.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(GetSolutionStatusTool.MaxLoadDiagnostics + 1, lines.Length);
		Assert.Equal("\tloadDiagnostic=message 000", lines[0]);
		Assert.Equal("\tloadDiagnosticsTruncated=5", lines[^1]);
	}

	[Fact]
	public void WhenTheLimitIsDescribed_ThenTheTextMatchesTheConstant()
	{
		Assert.Equal(GetSolutionStatusTool.MaxLoadDiagnosticsText, GetSolutionStatusTool.MaxLoadDiagnostics.ToString());
	}

	[Fact]
	public void WhenThereIsNoLimit_ThenEveryMessageIsListedWithNewlinesRemoved()
	{
		var builder = new OutlineBuilder();

		GetSolutionStatusTool.AppendMessages(builder, "loadDiagnostic", ["first\r\nsecond", "third"], limit: null);

		Assert.Equal("\tloadDiagnostic=first  second\n\tloadDiagnostic=third\n", builder.ToString());
	}

	[Fact]
	public void WhenTheSourceDroppedMessages_ThenTheTruncationLineCountsThemToo()
	{
		var builder = new OutlineBuilder();

		GetSolutionStatusTool.AppendMessages(builder, "watchDiagnostic", ["a", "b", "c"], limit: 2, alreadyDropped: 4);

		Assert.Equal("\twatchDiagnostic=a\n\twatchDiagnostic=b\n\twatchDiagnosticsTruncated=5\n", builder.ToString());
	}

	[Fact]
	public async Task WhenNoSolutionIdIsGiven_ThenEveryLoadedSolutionIsListed()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance simple = await registry.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance references = await registry.GetOrAddAsync(TestSolutions.References);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus();

		string[] lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		Assert.Equal(2, lines.Length);
		Assert.Contains(lines, line => line.StartsWith($"{simple.Key.SolutionId},", StringComparison.Ordinal));
		Assert.Contains(lines, line => line.StartsWith($"{references.Key.SolutionId},", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenASolutionIdIsGiven_ThenOnlyThatSolutionIsListed()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance simple = await registry.GetOrAddAsync(TestSolutions.Simple);
		RoslynInstance references = await registry.GetOrAddAsync(TestSolutions.References);
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus(solutionId: references.Key.SolutionId);

		string line = Assert.Single(result.Split('\n', StringSplitOptions.RemoveEmptyEntries));
		Assert.StartsWith($"{references.Key.SolutionId},", line);
		Assert.DoesNotContain(simple.Key.SolutionId, result);
	}

	[Fact]
	public async Task WhenASolutionIdIsAnAbsolutePathWithBackslashes_ThenThatSolutionIsSelected()
	{
		// A backslash path is absolute only on Windows; elsewhere the separator has another meaning.
		if (!OperatingSystem.IsWindows())
			return;

		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);
		string backslashPath = instance.Key.FilePath.Replace('/', '\\');

		string result = subject.GetSolutionStatus(solutionId: backslashPath);

		string line = Assert.Single(result.Split('\n', StringSplitOptions.RemoveEmptyEntries));
		Assert.StartsWith($"{instance.Key.SolutionId},", line);
	}

	[Fact]
	public async Task WhenTheSolutionIdIsNotLoaded_ThenGetSolutionStatusReturnsNotFoundWithTheLoadedIdsAsCandidates()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetSolutionStatusTool(registry);
		string requested = Path.Combine(Path.GetTempPath(), "not-loaded", "Other.slnx");

		string result = subject.GetSolutionStatus(solutionId: requested);

		Assert.Contains("error=NotFound", result);
		Assert.Contains($"No solution is loaded for '{requested}'", result);
		Assert.Contains($"candidate={instance.Key.SolutionId}", result);
		Assert.DoesNotContain(",Ready,", result);
	}

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	public void WhenTheSolutionIdIsEmptyOrWhitespace_ThenGetSolutionStatusReturnsInvalid(string solutionId)
	{
		using var registry = new InstanceRegistry();
		var subject = new GetSolutionStatusTool(registry);

		string result = subject.GetSolutionStatus(solutionId: solutionId);

		Assert.Contains("error=Invalid", result);
		Assert.Contains("solutionId", result);
	}
}
