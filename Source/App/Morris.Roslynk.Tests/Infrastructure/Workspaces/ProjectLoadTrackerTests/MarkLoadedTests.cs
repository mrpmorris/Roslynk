using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.ProjectLoadTrackerTests;

public class MarkLoadedTests
{
	[Fact]
	public void WhenTheSameProjectIsReportedTwice_ThenItCountsOnce()
	{
		var subject = new ProjectLoadTracker();

		subject.MarkLoaded(@"C:\app\Lib.csproj");
		subject.MarkLoaded(@"C:\app\Lib.csproj");

		Assert.Equal(1, subject.Count);
	}

	[Fact]
	public void WhenDifferentProjectsAreReported_ThenEachCounts()
	{
		var subject = new ProjectLoadTracker();

		subject.MarkLoaded(@"C:\app\One.csproj");
		subject.MarkLoaded(@"C:\app\Two.csproj");

		Assert.Equal(2, subject.Count);
	}

	[Fact]
	public void WhenFilePathIsNull_ThenItThrows()
	{
		var subject = new ProjectLoadTracker();

		Assert.Throws<ArgumentNullException>(() => subject.MarkLoaded(null!));
	}
}
