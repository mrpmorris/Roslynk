using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Infrastructure.Results.ErrorTests;

public class ErrorTests
{
	[Fact]
	public void WhenNotFoundWithCandidates_ThenTheCodeAndCandidatesAreCarried()
	{
		Error subject = Error.NotFound("nothing matched", new[] { "A", "B" });

		Assert.Equal(ErrorCode.NotFound, subject.Code);
		Assert.Equal(new[] { "A", "B" }, subject.Candidates);
		Assert.Null(subject.StaleFiles);
	}

	[Fact]
	public void WhenStale_ThenTheCodeAndStaleFilesAreCarried()
	{
		Error subject = Error.Stale("changed on disk", new[] { "Widget.cs" });

		Assert.Equal(ErrorCode.Stale, subject.Code);
		Assert.Equal(new[] { "Widget.cs" }, subject.StaleFiles);
		Assert.Null(subject.Candidates);
	}

	[Fact]
	public void WhenIndexing_ThenTheCodeIsIndexing()
	{
		Error subject = Error.Indexing();

		Assert.Equal(ErrorCode.Indexing, subject.Code);
		Assert.False(string.IsNullOrWhiteSpace(subject.Message));
	}

	[Fact]
	public void WhenNotSupported_ThenTheCodeIsNotSupported()
	{
		Error subject = Error.NotSupported("not a C# file");

		Assert.Equal(ErrorCode.NotSupported, subject.Code);
		Assert.Equal("not a C# file", subject.Message);
	}
}
