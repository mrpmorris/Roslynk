using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Tests.Infrastructure.Outlines;

/// <summary>
/// LineWindow survives issue 96 only as a line counter (get_symbol_body's parts overview still needs
/// it); slicing itself moved to <see cref="ItemPaging"/>.
/// </summary>
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
	public void WhenLinesAreEmptyOrUnbroken_ThenEachStillCounts()
	{
		Assert.Equal(1, LineWindow.CountLines("\n"));
		Assert.Equal(3, LineWindow.CountLines("a\n\nb"));
		Assert.Equal(3, LineWindow.CountLines("a\n\nb\n"));
	}

	[Fact]
	public void WhenCountingALongText_ThenEveryBreakCounts()
	{
		string text = string.Concat(Enumerable.Range(0, 1_000).Select(index => $"line {index:D5} has content\n"));
		Assert.Equal(1_000, LineWindow.CountLines(text));
	}
}

/// <summary>
/// The Fit backstop for the four structurally bounded tools that cannot page (get_symbol,
/// find_definition, get_expression_info, get_solution_status): an oversized result is cut after its
/// last whole line and marked. Issue 96 exempted multi_query from it - the envelope enforces the
/// budget with whole-or-drop selection - but the backstop itself stays.
/// </summary>
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
}
