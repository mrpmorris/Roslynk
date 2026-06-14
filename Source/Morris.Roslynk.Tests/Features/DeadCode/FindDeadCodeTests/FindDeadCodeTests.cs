using Morris.Roslynk.Features.DeadCode.FindDeadCode;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.DeadCode.FindDeadCodeTests;

public class FindDeadCodeTests
{
	[Fact]
	public async Task WhenAPrivateMethodIsNeverCalled_ThenItIsReportedWithHighConfidence()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResponse response = await subject.FindDeadCode(TestSolutions.Simple);

		DeadCodeCandidate unused = Assert.Single(response.Candidates, candidate => candidate.Symbol == "SimpleLibrary.Widget.Unused");
		Assert.Equal("High", unused.Confidence);
	}

	[Fact]
	public async Task WhenAMethodIsReferencedOrImplementsAnInterface_ThenItIsNotReported()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResponse response = await subject.FindDeadCode(TestSolutions.Simple, includePublic: true);

		Assert.DoesNotContain(response.Candidates, candidate => candidate.Symbol == "SimpleLibrary.Widget.Compute");
		Assert.DoesNotContain(response.Candidates, candidate => candidate.Symbol == "SimpleLibrary.Greeter.Greet");
	}

	[Fact]
	public async Task WhenIncludePublicIsFalse_ThenUnreferencedPublicMembersAreNotReported()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResponse response = await subject.FindDeadCode(TestSolutions.Simple, includePublic: false);

		Assert.DoesNotContain(response.Candidates, candidate => candidate.Symbol == "SimpleLibrary.Caller.Run");
	}

	[Fact]
	public async Task WhenIncludePublicIsTrue_ThenUnreferencedPublicMembersAreReported()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResponse response = await subject.FindDeadCode(TestSolutions.Simple, includePublic: true);

		Assert.Contains(response.Candidates, candidate => candidate.Symbol == "SimpleLibrary.Caller.Run");
	}

	[Fact]
	public async Task WhenAScopeIsGiven_ThenOnlyMatchingSymbolsAreConsidered()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		FindDeadCodeResponse response = await subject.FindDeadCode(TestSolutions.Simple, scope: "SimpleLibrary.Widget");

		Assert.NotEmpty(response.Candidates);
		Assert.All(response.Candidates, candidate => Assert.StartsWith("SimpleLibrary.Widget", candidate.Symbol));
	}
}
