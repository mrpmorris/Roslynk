using System.Text;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Outlines;

/// <summary>
/// Builds a tool's compact text result: '#key=value' header lines, a blank line, then a tab-indented body.
/// Newlines are always '\n' (never '\r'); a header or free-text value that could carry a line break is
/// sanitized so one record always stays on one line. This is the shared shape behind every tool's output,
/// replacing the per-tool JSON DTOs.
/// </summary>
public sealed class OutlineBuilder
{
	/// <summary>Separates the locations inside a single comma-delimited leaf, so the list is unambiguous.</summary>
	public const char LocationSeparator = '|';

	private readonly StringBuilder Builder = new();
	private bool BodyStarted;

	public OutlineBuilder Header(string key, string? value)
	{
		Builder.Append('#').Append(key).Append('=').Append(Sanitize(value)).Append('\n');
		return this;
	}

	public OutlineBuilder Header(string key, int value)
	{
		Builder.Append('#').Append(key).Append('=').Append(value).Append('\n');
		return this;
	}

	public OutlineBuilder Header(string key, bool value)
	{
		Builder.Append('#').Append(key).Append('=').Append(value ? "Y" : "N").Append('\n');
		return this;
	}

	/// <summary>
	/// Writes the '#status' header only when the solution is not <see cref="SolutionStatus.Ready"/>; a Ready
	/// solution is the common case, so its status is left implicit (an absent '#status' means Ready).
	/// </summary>
	public OutlineBuilder Status(SolutionStatus status) =>
		status == SolutionStatus.Ready ? this : Header("status", status.ToString());

	/// <summary>
	/// Writes the blank line that separates the header from the body. A no-op after the first call, and a
	/// no-op when nothing has been written yet, so a header-only-empty result starts straight at the body
	/// rather than with a stray leading blank line.
	/// </summary>
	public OutlineBuilder BeginBody()
	{
		if (!BodyStarted)
		{
			if (Builder.Length > 0)
				Builder.Append('\n');

			BodyStarted = true;
		}

		return this;
	}

	/// <summary>Writes a body line at the given tab depth (0 = no indent).</summary>
	public OutlineBuilder Line(int depth, string text)
	{
		for (int i = 0; i < depth; i++)
			Builder.Append('\t');

		Builder.Append(text).Append('\n');
		return this;
	}

	public override string ToString() => Builder.ToString();

	/// <summary>
	/// Encodes a name for use as one field of a comma-delimited leaf: a name that itself contains a comma (a
	/// generic type rendered with several type arguments, e.g. Dictionary&lt;string, int&gt;) is wrapped in
	/// single quotes so the comma is not read as a field separator; any other name is returned unchanged.
	/// </summary>
	public static string Field(string value) =>
		value.Contains(',') ? $"'{value}'" : value;

	/// <summary>
	/// Replaces any CR or LF with a space so a free-text value (an error or diagnostic message, a code-action
	/// title) cannot break the one-record-per-line shape. Null or empty passes through.
	/// </summary>
	public static string Sanitize(string? value)
	{
		if (string.IsNullOrEmpty(value))
			return value ?? "";

		return value.Replace('\r', ' ').Replace('\n', ' ');
	}
}
