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
			SolutionStatus.Ready,
			"3");

		Assert.Equal(
			"#error=Ambiguous\n#errorMessage='X' matched several symbols.\n#candidate=N.A\n#candidate=N.B\n#status=Ready\n#snapshot=3\n",
			result);
	}

	[Fact]
	public void WhenStale_ThenEachStaleFileIsItsOwnRepeatableHeader()
	{
		string result = OutlineError.Format(
			Error.Stale("Files moved on disk.", ["src/A.cs", "src/B.cs"]),
			SolutionStatus.Ready,
			"4");

		Assert.Equal(
			"#error=Stale\n#errorMessage=Files moved on disk.\n#stale=src/A.cs\n#stale=src/B.cs\n#status=Ready\n#snapshot=4\n",
			result);
	}

	[Fact]
	public void WhenTheMessageContainsNewlines_ThenTheyAreCollapsedToKeepOneLine()
	{
		string result = OutlineError.Format(
			Error.Invalid("first\r\nsecond"),
			SolutionStatus.Building,
			"1");

		Assert.DoesNotContain("\r", result);
		Assert.Contains("#errorMessage=first  second\n", result);
	}
}
