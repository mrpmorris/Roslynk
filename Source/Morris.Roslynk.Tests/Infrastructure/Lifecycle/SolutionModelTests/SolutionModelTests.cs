using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.SolutionModelTests;

public class SolutionModelTests
{
	[Fact]
	public void WhenLoading_ThenStatusIsBuildingAndSolutionIsNull()
	{
		SolutionModel subject = SolutionModel.Loading(solution: null);

		Assert.Equal(SolutionStatus.Building, subject.Status);
		Assert.Null(subject.Solution);
	}

	[Fact]
	public void WhenFaulted_ThenStatusIsFaultedAndTheMessageIsKept()
	{
		SolutionModel subject = SolutionModel.Faulted("load failed");

		Assert.Equal(SolutionStatus.Faulted, subject.Status);
		Assert.Equal("load failed", subject.FaultMessage);
		Assert.Null(subject.Solution);
	}
}
