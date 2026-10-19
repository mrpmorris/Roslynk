using System.Text;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// A call named an argument its tool does not declare. Deliberately its own exception type so the shared
/// exception-to-<see cref="Error"/> rule can report <see cref="ErrorCode.UnknownArgument"/>
/// rather than the generic <see cref="ErrorCode.Invalid"/> a binding failure gets.
/// </summary>
internal sealed class UnknownArgumentException : ArgumentException
{
	public UnknownArgumentException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// The one unknown-argument check, shared by the MCP wrapper (every published tool) and the multi_query
/// binder (every operation), so both refuse a misspelled argument with the same message and error code.
/// The valid names are the tool's declared parameters, in declaration order.
/// </summary>
internal static class UnknownArgument
{
	/// <summary>
	/// Throws <see cref="UnknownArgumentException"/> when any supplied key is not one of
	/// <paramref name="valid"/>. Comparison is ordinal: an argument name is the published schema property,
	/// and anything else is exactly the mistake this check exists to surface.
	/// </summary>
	public static void Reject(IEnumerable<string> supplied, IReadOnlyList<string> valid, string toolName)
	{
		var unknown = new List<string>();
		var known = new HashSet<string>(valid, StringComparer.Ordinal);
		foreach (string key in supplied)
		{
			if (!known.Contains(key))
				unknown.Add(key);
		}

		if (unknown.Count == 0)
			return;

		throw new UnknownArgumentException(Message(unknown, valid, toolName));
	}

	private static string Message(IReadOnlyList<string> unknown, IReadOnlyList<string> valid, string toolName)
	{
		var builder = new StringBuilder();
		builder.Append(unknown.Count == 1 ? "Unknown argument " : "Unknown arguments ");
		for (int index = 0; index < unknown.Count; index++)
		{
			if (index > 0)
				builder.Append(index == unknown.Count - 1 ? " and " : ", ");
			builder.Append('\'').Append(unknown[index]).Append('\'');
		}
		builder.Append(" for ").Append(toolName).Append('.');

		if (valid.Count == 0)
		{
			builder.Append(" The tool takes no arguments.");
			return builder.ToString();
		}

		builder.Append(" Valid: ").Append(string.Join(", ", valid)).Append('.');

		if (unknown.Count == 1 && Suggest(unknown[0], valid) is { } suggestion)
			builder.Append(" Did you mean '").Append(suggestion).Append("'?");

		return builder.ToString();
	}

	/// <summary>
	/// The closest valid name when it is a plausible typo: within two edits and the only candidate at that
	/// distance. Beyond that the guess is noise, so no suggestion is made.
	/// </summary>
	private static string? Suggest(string unknown, IReadOnlyList<string> valid)
	{
		const int MaximumDistance = 2;
		string? best = null;
		int bestDistance = int.MaxValue;
		bool tied = false;

		foreach (string candidate in valid)
		{
			int distance = Distance(unknown, candidate);
			if (distance < bestDistance)
			{
				bestDistance = distance;
				best = candidate;
				tied = false;
			}
			else if (distance == bestDistance)
			{
				tied = true;
			}
		}

		return bestDistance <= MaximumDistance && !tied ? best : null;
	}

	/// <summary>Case-insensitive Levenshtein distance, two rows, so a suggestion costs nothing at call time.</summary>
	private static int Distance(string left, string right)
	{
		if (left.Length == 0)
			return right.Length;
		if (right.Length == 0)
			return left.Length;

		var previous = new int[right.Length + 1];
		var current = new int[right.Length + 1];
		for (int column = 0; column <= right.Length; column++)
			previous[column] = column;

		for (int row = 1; row <= left.Length; row++)
		{
			current[0] = row;
			for (int column = 1; column <= right.Length; column++)
			{
				int cost = char.ToLowerInvariant(left[row - 1]) == char.ToLowerInvariant(right[column - 1]) ? 0 : 1;
				current[column] = Math.Min(
					Math.Min(previous[column] + 1, current[column - 1] + 1),
					previous[column - 1] + cost);
			}
			(previous, current) = (current, previous);
		}

		return previous[right.Length];
	}
}
