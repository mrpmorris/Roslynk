using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Infrastructure.Outlines;

public class OutlineErrorTests
{
	[Fact]
	public void WhenAmbiguous_ThenEachCandidateIsItsOwnRepeatableHeader()
	{
		string result = OutlineError.Format(
			Error.Ambiguous("'X' matched several symbols.", ["N.A", "N.B"]),
			SolutionStatus.Ready);

		Assert.Equal(
			"error=Ambiguous\nerrorMessage='X' matched several symbols.\ncandidate=N.A\ncandidate=N.B\n",
			result);
	}

	[Fact]
	public void WhenStale_ThenEachStaleFileIsItsOwnRepeatableHeader()
	{
		string result = OutlineError.Format(
			Error.Stale("Files moved on disk.", ["src/A.cs", "src/B.cs"]),
			SolutionStatus.Ready);

		Assert.Equal(
			"error=Stale\nerrorMessage=Files moved on disk.\nstale=src/A.cs\nstale=src/B.cs\n",
			result);
	}

	[Fact]
	public void WhenTheMessageContainsNewlines_ThenTheyAreCollapsedToKeepOneLine()
	{
		string result = OutlineError.Format(
			Error.Invalid("first\r\nsecond"),
			SolutionStatus.Building);

		Assert.DoesNotContain("\r", result);
		Assert.Contains("errorMessage=first  second\n", result);
	}
}
