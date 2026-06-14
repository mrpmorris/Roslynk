using Morris.Roslynk.Features.DeadCode.FindDeadCode;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.DeadCode.FindDeadCodeTests;

public class FindDeadCodeTests
{
	[Fact]
	public async Task WhenAPrivateMethodIsNeverCalled_ThenItIsReportedWithHighConfidence()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResult result = await subject.FindDeadCode(TestSolutions.Simple);

		Assert.True(result.IsSuccess);
		DeadCodeCandidate unused = Assert.Single(result.Candidates!, candidate => candidate.Symbol == "SimpleLibrary.Widget.Unused");
		Assert.Equal("High", unused.Confidence);
	}

	[Fact]
	public async Task WhenAMethodIsReferencedOrImplementsAnInterface_ThenItIsNotReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResult result = await subject.FindDeadCode(TestSolutions.Simple, includePublic: true);

		Assert.True(result.IsSuccess);
		Assert.DoesNotContain(result.Candidates!, candidate => candidate.Symbol == "SimpleLibrary.Widget.Compute");
		Assert.DoesNotContain(result.Candidates!, candidate => candidate.Symbol == "SimpleLibrary.Greeter.Greet");
	}

	[Fact]
	public async Task WhenIncludePublicIsFalse_ThenUnreferencedPublicMembersAreNotReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResult result = await subject.FindDeadCode(TestSolutions.Simple, includePublic: false);

		Assert.True(result.IsSuccess);
		Assert.DoesNotContain(result.Candidates!, candidate => candidate.Symbol == "SimpleLibrary.Caller.Run");
	}

	[Fact]
	public async Task WhenIncludePublicIsTrue_ThenUnreferencedPublicMembersAreReported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResult result = await subject.FindDeadCode(TestSolutions.Simple, includePublic: true);

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Candidates!, candidate => candidate.Symbol == "SimpleLibrary.Caller.Run");
	}

	[Fact]
	public async Task WhenAScopeIsGiven_ThenOnlyMatchingSymbolsAreConsidered()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResult result = await subject.FindDeadCode(TestSolutions.Simple, scope: "SimpleLibrary.Widget");

		Assert.True(result.IsSuccess);
		Assert.NotEmpty(result.Candidates!);
		Assert.All(result.Candidates!, candidate => Assert.StartsWith("SimpleLibrary.Widget", candidate.Symbol));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResult result = await subject.FindDeadCode(TestSolutions.Simple);

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
