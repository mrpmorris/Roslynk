using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Infrastructure.Results.ResultBaseTests;

public class ResultBaseTests
{
	private sealed class SampleResult : ResultBase
	{
		public SampleResult(string solutionCurrentSnapshotId, SolutionStatus status, Error? error)
			: base(solutionCurrentSnapshotId, status, error)
		{
		}
	}

	[Fact]
	public void WhenThereIsNoError_ThenIsSuccessIsTrueAndTheSnapshotIdIsCarried()
	{
		var subject = new SampleResult("7", SolutionStatus.Building, error: null);

		Assert.True(subject.IsSuccess);
		Assert.Equal("7", subject.SolutionCurrentSnapshotId);
		Assert.Equal(SolutionStatus.Building, subject.Status);
	}

	[Fact]
	public void WhenThereIsAnError_ThenIsSuccessIsFalse()
	{
		var subject = new SampleResult("7", SolutionStatus.Building, Error.Invalid("bad"));

		Assert.False(subject.IsSuccess);
	}

	[Fact]
	public void WhenTheSnapshotIdIsNull_ThenTheConstructorThrows()
	{
		Assert.Throws<ArgumentNullException>(() => new SampleResult(null!, SolutionStatus.Building, error: null));
	}
}
