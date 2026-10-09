using Morris.Roslynk.Features.Solutions.GetSolutionStatus;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Tests.Features.Solutions.GetSolutionStatusTests;

public class GetSolutionStatusTests
{
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

		GetSolutionStatusTool.AppendLoadDiagnostics(builder, messages, GetSolutionStatusTool.MaxLoadDiagnostics);

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

		GetSolutionStatusTool.AppendLoadDiagnostics(builder, ["first\r\nsecond", "third"], limit: null);

		Assert.Equal("\tloadDiagnostic=first  second\n\tloadDiagnostic=third\n", builder.ToString());
	}
}
