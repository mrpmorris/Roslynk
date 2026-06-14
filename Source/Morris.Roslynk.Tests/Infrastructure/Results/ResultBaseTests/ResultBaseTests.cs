using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Infrastructure.Results.ResultBaseTests;

public class ResultBaseTests
{
	private sealed record SampleResult : ResultBase
	{
		public SampleResult(SolutionModel solutionModel, Error? error)
			: base(solutionModel, error)
		{
		}
	}

	[Fact]
	public void WhenThereIsNoError_ThenIsSuccessIsTrueAndTheSnapshotIdIsCarried()
	{
		var subject = new SampleResult(SolutionModel.Loading("7", solution: null), error: null);

		Assert.True(subject.IsSuccess);
		Assert.Equal("7", subject.SolutionCurrentSnapshotId);
		Assert.Equal(SolutionStatus.Building, subject.Status);
	}

	[Fact]
	public void WhenThereIsAnError_ThenIsSuccessIsFalse()
	{
		var subject = new SampleResult(SolutionModel.Loading("7", solution: null), Error.Invalid("bad"));

		Assert.False(subject.IsSuccess);
	}
}
