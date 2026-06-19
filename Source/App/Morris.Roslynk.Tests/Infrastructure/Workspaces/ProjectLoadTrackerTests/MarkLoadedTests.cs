using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.ProjectLoadTrackerTests;

public class MarkLoadedTests
{
	[Fact]
	public void WhenTheSameProjectAndFrameworkAreReportedTwice_ThenItCountsOnce()
	{
		var subject = new ProjectLoadTracker();

		subject.MarkLoaded(@"C:\app\Lib.csproj", "net10.0");
		subject.MarkLoaded(@"C:\app\Lib.csproj", "net10.0");

		Assert.Equal(1, subject.Count);
	}

	[Fact]
	public void WhenAProjectIsReportedForTwoFrameworks_ThenEachCounts()
	{
		var subject = new ProjectLoadTracker();

		subject.MarkLoaded(@"C:\app\Lib.csproj", "net8.0");
		subject.MarkLoaded(@"C:\app\Lib.csproj", "net10.0");

		Assert.Equal(2, subject.Count);
	}

	[Fact]
	public void WhenDifferentProjectsAreReported_ThenEachCounts()
	{
		var subject = new ProjectLoadTracker();

		subject.MarkLoaded(@"C:\app\One.csproj", null);
		subject.MarkLoaded(@"C:\app\Two.csproj", null);

		Assert.Equal(2, subject.Count);
	}

	[Fact]
	public void WhenFilePathIsNull_ThenItThrows()
	{
		var subject = new ProjectLoadTracker();

		Assert.Throws<ArgumentNullException>(() => subject.MarkLoaded(null!, null));
	}
}
