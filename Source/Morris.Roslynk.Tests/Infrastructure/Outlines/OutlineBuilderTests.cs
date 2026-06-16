using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Tests.Infrastructure.Outlines;

public class OutlineBuilderTests
{
	[Fact]
	public void WhenHeadersAndBodyAreWritten_ThenABlankLineSeparatesThem()
	{
		var subject = new OutlineBuilder();

		subject.Header("count", 2);
		subject.Status(SolutionStatus.Ready);
		subject.Snapshot("7");
		subject.BeginBody();
		subject.Line(0, "first");
		subject.Line(1, "child");

		Assert.Equal("#count=2\n#status=Ready\n#snapshot=7\n\nfirst\n\tchild\n", subject.ToString());
	}

	[Fact]
	public void WhenBeginBodyIsCalledTwice_ThenOnlyOneBlankLineIsWritten()
	{
		var subject = new OutlineBuilder();

		subject.Header("a", "b");
		subject.BeginBody();
		subject.BeginBody();
		subject.Line(0, "x");

		Assert.Equal("#a=b\n\nx\n", subject.ToString());
	}

	[Fact]
	public void WhenABooleanHeaderIsWritten_ThenItIsLowerCaseTrueOrFalse()
	{
		var subject = new OutlineBuilder();

		subject.Header("truncated", true);
		subject.Header("applied", false);

		Assert.Equal("#truncated=true\n#applied=false\n", subject.ToString());
	}

	[Theory]
	[InlineData("a\r\nb", "a  b")]
	[InlineData("a\nb", "a b")]
	[InlineData("a\rb", "a b")]
	public void WhenAValueContainsLineBreaks_ThenSanitizeReplacesThemWithSpaces(string input, string expected)
	{
		Assert.Equal(expected, OutlineBuilder.Sanitize(input));
	}

	[Fact]
	public void WhenAHeaderValueContainsANewline_ThenItIsCollapsedSoTheRecordStaysOnOneLine()
	{
		var subject = new OutlineBuilder();

		subject.Header("errorMessage", "line one\nline two");

		Assert.Equal("#errorMessage=line one line two\n", subject.ToString());
	}

	[Fact]
	public void WhenSanitizingNull_ThenAnEmptyStringIsReturned()
	{
		Assert.Equal("", OutlineBuilder.Sanitize(null));
	}
}
