using System.Text.RegularExpressions;

namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// The most characters one tool result may carry. MCP has no protocol-level size limit, but clients do
/// (Claude Code rejects results over MAX_MCP_OUTPUT_TOKENS, 25,000 tokens by default), so every response
/// is held under this one server-wide figure. multi_query reserves its framing against it and divides the
/// remainder between slots; a core that declares a <see cref="ResponseBudget"/> parameter receives its
/// slot's share and pages itself; <see cref="Fit"/> cuts any other oversized output at a line boundary as
/// a last resort.
/// </summary>
public sealed partial class ResponseBudget
{
	/// <summary>About 20K tokens of source at 4 chars/token; inside the client cap with headroom.</summary>
	public const int DefaultMaxChars = 80_000;

	/// <summary>
	/// The smallest budget that still frames a full 25-operation batch and runs its first slot; a smaller
	/// configured value is raised to this.
	/// </summary>
	public const int MinimumMaxChars = 8_000;

	public static ResponseBudget Default { get; } = new(DefaultMaxChars);

	public int MaxChars { get; private set; }

	public ResponseBudget(int maxChars) => MaxChars = Math.Max(MinimumMaxChars, maxChars);

	/// <summary>A per-slot share of the budget; not clamped, because multi_query hands out exact remainders.</summary>
	internal static ResponseBudget Allowance(int maxChars) => new() { MaxChars = maxChars };

	private ResponseBudget() { }

	/// <summary>
	/// Returns <paramref name="output"/> unchanged when it fits; otherwise the longest prefix ending after a
	/// whole line, preceded by 'outputTruncated=Y' and 'fullOutputChars=&lt;n&gt;' headers, all within
	/// <paramref name="maxChars"/>. A line break is never split; the char-level fallback steps back over a
	/// high surrogate or a '\r' rather than tearing either.
	/// </summary>
	public static string Fit(string output, int maxChars)
	{
		if (output.Length <= maxChars)
			return output;

		// Inserted ahead of the body. An output with no header block of its own (body only) needs the blank
		// line that separates headers from body, or its first body line would read as a header.
		string headers = $"outputTruncated=Y\nfullOutputChars={output.Length}\n";
		if (!HeaderLine().IsMatch(output))
			headers += "\n";

		int room = maxChars - headers.Length;
		int searchFrom = Math.Min(room, output.Length) - 1;
		int lastBreak = searchFrom >= 0 ? output.LastIndexOf('\n', searchFrom) : -1;
		int end = lastBreak >= 0 ? lastBreak + 1 : SafeCut(output, 0, Math.Max(0, room));
		return headers + output[..end];
	}

	/// <summary>
	/// The length of the longest prefix of <paramref name="text"/> starting at <paramref name="start"/> that
	/// is at most <paramref name="length"/> characters and does not end halfway through a surrogate pair or
	/// a CRLF.
	/// </summary>
	public static int SafeCut(string text, int start, int length)
	{
		int end = start + length;
		if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]))
			end--;
		if (end < text.Length && end > start && text[end - 1] == '\r' && text[end] == '\n')
			end--;
		return end - start;
	}

	[GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9]*=")]
	private static partial Regex HeaderLine();
}
