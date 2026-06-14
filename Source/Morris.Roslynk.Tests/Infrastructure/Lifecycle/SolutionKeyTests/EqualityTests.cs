using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Infrastructure.Lifecycle.SolutionKeyTests;

public class EqualityTests
{
	[Fact]
	public void WhenPathsDifferOnlyInCase_ThenKeysAreEqual()
	{
		SolutionKey first = SolutionKey.For(@"C:\Solutions\App\App.slnx");
		SolutionKey second = SolutionKey.For(@"C:\solutions\app\APP.SLNX");

		Assert.Equal(first, second);
		Assert.Equal(first.GetHashCode(), second.GetHashCode());
	}

	[Fact]
	public void WhenGivenARelativePath_ThenTheKeyIsMadeAbsolute()
	{
		SolutionKey subject = SolutionKey.For("App.slnx");

		Assert.True(System.IO.Path.IsPathFullyQualified(subject.Path));
	}
}
