using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Infrastructure.Results.ResultBaseTests;

public class ResultBaseTests
{
	private sealed record SampleResult : ResultBase;

	[Fact]
	public void WhenThereIsNoError_ThenIsSuccessIsTrue()
	{
		var subject = new SampleResult { SnapshotId = "1", Status = SolutionStatus.Ready };

		Assert.True(subject.IsSuccess);
	}

	[Fact]
	public void WhenThereIsAnError_ThenIsSuccessIsFalse()
	{
		var subject = new SampleResult { SnapshotId = "1", Status = SolutionStatus.Ready, Error = Error.Invalid("bad") };

		Assert.False(subject.IsSuccess);
	}
}
