using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.SolutionModelTests;

public class SolutionModelTests
{
	[Fact]
	public void WhenLoadingWithoutASnapshot_ThenStatusIsBuildingAndSolutionIsNull()
	{
		SolutionModel subject = SolutionModel.Loading("1", solution: null);

		Assert.Equal(SolutionStatus.Building, subject.Status);
		Assert.Null(subject.Solution);
		Assert.Equal("1", subject.SnapshotId);
	}

	[Fact]
	public void WhenFaulted_ThenStatusIsFaultedAndTheMessageIsKept()
	{
		SolutionModel subject = SolutionModel.Faulted("2", "load failed");

		Assert.Equal(SolutionStatus.Faulted, subject.Status);
		Assert.Equal("load failed", subject.FaultMessage);
		Assert.Null(subject.Solution);
	}
}
