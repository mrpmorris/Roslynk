using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.SolutionKeyTests;

public class EqualityTests
{
	[Fact]
	public void WhenPathsDifferOnlyInCase_ThenEqualityFollowsThePlatformCasePolicy()
	{
		string firstPath = OperatingSystem.IsWindows() ? @"C:\Solutions\App\App.slnx" : "/Solutions/App/App.slnx";
		string secondPath = OperatingSystem.IsWindows() ? @"C:\solutions\app\APP.SLNX" : "/solutions/app/APP.SLNX";

		SolutionKey first = SolutionKey.For(firstPath);
		SolutionKey second = SolutionKey.For(secondPath);

		bool expectEqual = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

		Assert.Equal(expectEqual, first.Equals(second));
		if (expectEqual)
			Assert.Equal(first.GetHashCode(), second.GetHashCode());
	}

	[Fact]
	public void WhenGivenARelativePath_ThenTheKeyIsMadeAbsolute()
	{
		SolutionKey subject = SolutionKey.For("App.slnx");

		Assert.True(System.IO.Path.IsPathFullyQualified(subject.FilePath));
	}

	[Fact]
	public void WhenTheKeyIsEchoed_ThenTheSolutionIdUsesForwardSlashesWithoutChangingTheIdentity()
	{
		string path = OperatingSystem.IsWindows() ? @"C:\Solutions\App\App.slnx" : "/Solutions/App/App.slnx";

		SolutionKey subject = SolutionKey.For(path);

		Assert.Equal(System.IO.Path.GetFullPath(path).Replace('\\', '/'), subject.SolutionId);
		Assert.Equal(System.IO.Path.GetFullPath(path), subject.FilePath);
	}
}
