using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Tests.Infrastructure.Outlines;

public class LineWindowTests
{
	[Fact]
	public void WhenTextHasNoTrailingNewline_ThenTheFinalLineIsCounted()
	{
		Assert.Equal(0, LineWindow.CountLines(""));
		Assert.Equal(1, LineWindow.CountLines("one"));
		Assert.Equal(2, LineWindow.CountLines("one\ntwo"));
		Assert.Equal(2, LineWindow.CountLines("one\ntwo\n"));
		Assert.Equal(2, LineWindow.CountLines("one\r\ntwo\r\n"));
	}

	[Fact]
	public void WhenTheWindowFits_ThenThePageIsTheWholeTextAndNothingIsMarked()
	{
		var (page, totalLines, more, nextStartLine, partialLine) = LineWindow.Slice("a\r\nb\r\nc", 1, 10, 100);

		Assert.Equal("a\r\nb\r\nc", page);
		Assert.Equal(3, totalLines);
		Assert.False(more);
		Assert.Equal(4, nextStartLine);
		Assert.Equal(0, partialLine);
	}

	[Fact]
	public void WhenMaxLinesBinds_ThenTheCutLandsAfterAWholeLine()
	{
		var (page, _, more, nextStartLine, partialLine) = LineWindow.Slice("a\r\nb\r\nc", 1, 2, 100);

		Assert.Equal("a\r\nb\r\n", page); // the CRLF stays intact; the page is a byte-exact slice
		Assert.True(more);
		Assert.Equal(3, nextStartLine);
		Assert.Equal(0, partialLine);
	}

	[Fact]
	public void WhenMaxCharsBindsBeforeMaxLines_ThenThePageIsShorterStill()
	{
		// Three 5-char lines fit maxLines=10, but the 9-char ceiling fits only the first.
		var (page, _, more, nextStartLine, _) = LineWindow.Slice("aaaa\nbbbb\ncccc\n", 1, 10, 9);

		Assert.Equal("aaaa\n", page);
		Assert.True(more);
		Assert.Equal(2, nextStartLine);
	}

	[Fact]
	public void WhenStartLineContinues_ThenThePageStartsExactlyThere()
	{
		var (page, _, more, nextStartLine, _) = LineWindow.Slice("one\ntwo\nthree\n", 2, 1, 100);

		Assert.Equal("two\n", page);
		Assert.True(more);
		Assert.Equal(3, nextStartLine);
	}

	[Fact]
	public void WhenASingleLineExceedsTheWholeCeiling_ThenAsMuchAsFitsIsDeliveredAndNamed()
	{
		string text = new('x', 100);
		var (page, totalLines, more, nextStartLine, partialLine) = LineWindow.Slice(text, 1, 10, 40);

		Assert.Equal(new string('x', 40), page);
		Assert.Equal(1, totalLines);
		Assert.True(more);
		Assert.Equal(2, nextStartLine);
		Assert.Equal(1, partialLine);
	}

	[Fact]
	public void WhenASafeCutWouldSplitASurrogatePairOrACrlf_ThenItStepsBack()
	{
		// A high surrogate followed by a low surrogate: cutting after the high surrogate would tear the pair.
		string surrogate = "a" + char.ConvertFromUtf32(0x1F600) + "b";
		Assert.Equal(1, ResponseBudget.SafeCut(surrogate, 0, 2)); // 'a' only; the pair is indivisible
		Assert.Equal(3, ResponseBudget.SafeCut(surrogate, 0, 3));

		// A CRLF: cutting between the '\r' and the '\n' would tear the line ending.
		Assert.Equal(1, ResponseBudget.SafeCut("a\r\nb", 0, 2));
	}

	[Fact]
	public void WhenTheBudgetEndsTheWindow_ThenThePageStaysWithinIt()
	{
		string text = string.Concat(Enumerable.Range(0, 1_000).Select(index => $"line {index:D5} has content\n"));
		var (page, totalLines, more, nextStartLine, _) = LineWindow.Slice(text, 1, 1_000, 2_000);

		Assert.True(page.Length <= 2_000, $"Page was {page.Length} chars.");
		Assert.True(more);
		Assert.True(totalLines == 1_000);
		Assert.True(nextStartLine > 1);
	}
}

public class ResponseBudgetTests
{
	[Fact]
	public void WhenTheConfiguredValueIsBelowTheMinimum_ThenItIsClampedUp()
	{
		Assert.Equal(ResponseBudget.MinimumMaxChars, new ResponseBudget(0).MaxChars);
		Assert.Equal(ResponseBudget.MinimumMaxChars, new ResponseBudget(1).MaxChars);
		Assert.Equal(ResponseBudget.DefaultMaxChars, ResponseBudget.Default.MaxChars);
		Assert.Equal(12_345, new ResponseBudget(12_345).MaxChars);
	}

	[Fact]
	public void WhenOutputFits_ThenFitReturnsItUnchanged()
	{
		string output = "path=a.cs\n\nbody\n";

		Assert.Same(output, ResponseBudget.Fit(output, 1_000));
	}

	[Fact]
	public void WhenOutputExceedsTheBudget_ThenItIsCutAfterItsLastWholeLineAndMarked()
	{
		string output = "path=a.cs\n\n" + string.Concat(Enumerable.Repeat("0123456789\n", 100)); // 1,111 chars

		string cut = ResponseBudget.Fit(output, 500);

		Assert.StartsWith("outputTruncated=Y\n", cut);
		Assert.Contains("fullOutputChars=1111\n", cut);
		Assert.True(cut.Length <= 500, $"Cut was {cut.Length} chars.");
		Assert.EndsWith("\n", cut);

		// The kept part is a byte-exact prefix of the original: whole lines only, nothing reformatted.
		string headers = cut[..(cut.IndexOf("\npath=a.cs", StringComparison.Ordinal) + 1)];
		Assert.Equal(output[..(cut.Length - headers.Length)], cut[headers.Length..]);
	}

	[Fact]
	public void WhenBodyOnlyOutputIsCut_ThenABlankLineSeparatesTheHeadersFromTheFragment()
	{
		string output = string.Concat(Enumerable.Repeat("x", 2_000)); // no headers, no line break

		string cut = ResponseBudget.Fit(output, 1_000);

		Assert.StartsWith("outputTruncated=Y\nfullOutputChars=2000\n\n", cut);
		Assert.True(cut.Length <= 1_000);
	}

	[Fact]
	public void WhenTheBudgetIsMinimum_ThenAHugeOutputIsStillBoundedAndMarked()
	{
		string output = new('y', 250_000);

		string cut = ResponseBudget.Fit(output, ResponseBudget.MinimumMaxChars);

		Assert.True(cut.Length <= ResponseBudget.MinimumMaxChars);
		Assert.StartsWith("outputTruncated=Y\n", cut);
	}
}
