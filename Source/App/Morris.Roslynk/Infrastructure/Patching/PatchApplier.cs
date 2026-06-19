namespace Morris.Roslynk.Infrastructure.Patching;

/// <summary>
/// Applies a <see cref="FilePatch"/> to a file's text by content, not by trusting line numbers: each
/// hunk's old side (context + removed lines) is located in the current text and replaced with its new side.
/// A hunk whose header carried line numbers is anchored at that line first, then searched outward; a bare
/// (content-anchored) hunk must match exactly one place, otherwise it is rejected as ambiguous rather than
/// guessed. Matching is exact on content, so a hunk whose surroundings have genuinely changed fails rather
/// than corrupting the file. The file's own newline style and trailing-newline state are preserved
/// regardless of the patch's line endings.
/// </summary>
public static class PatchApplier
{
	public static PatchApplyResult Apply(string original, FilePatch patch)
	{
		if (original is null)
			throw new ArgumentNullException(nameof(original));
		if (patch is null)
			throw new ArgumentNullException(nameof(patch));

		string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
		bool endsWithNewline = original.Length > 0 && original.EndsWith('\n');
		List<string> lines = SplitLines(original);

		int offset = 0;
		foreach (Hunk hunk in patch.Hunks)
		{
			var oldBlock = new List<string>();
			var newBlock = new List<string>();
			foreach (HunkLine hunkLine in hunk.Lines)
			{
				if (hunkLine.Kind != HunkLineKind.Added)
					oldBlock.Add(hunkLine.Text);
				if (hunkLine.Kind != HunkLineKind.Removed)
					newBlock.Add(hunkLine.Text);
			}

			int at;
			if (hunk.HasExplicitPosition)
			{
				int hint = hunk.OldStart - 1 + offset;
				at = FindBlock(lines, oldBlock, hint);
				if (at < 0)
					return PatchApplyResult.Fail(
						$"Hunk @@ -{hunk.OldStart},{hunk.OldLength} +{hunk.NewStart},{hunk.NewLength} @@ did not match the current file content.");
			}
			else
			{
				if (oldBlock.Count == 0)
					return PatchApplyResult.Fail(
						"A content-anchored hunk with no context or removed lines cannot be located; add context lines or include @@ line numbers.");

				(int index, int matchCount) = FindUniqueBlock(lines, oldBlock);
				if (matchCount == 0)
					return PatchApplyResult.Fail("A content-anchored hunk did not match the current file content.");
				if (matchCount > 1)
					return PatchApplyResult.Fail(
						$"A content-anchored hunk matched {matchCount} locations; add more context lines or include @@ line numbers to disambiguate.");

				at = index;
			}

			lines.RemoveRange(at, oldBlock.Count);
			lines.InsertRange(at, newBlock);
			offset += newBlock.Count - oldBlock.Count;
		}

		string result = string.Join(newline, lines);
		if (endsWithNewline && result.Length > 0)
			result += newline;

		return PatchApplyResult.Ok(result);
	}

	private static List<string> SplitLines(string text)
	{
		if (text.Length == 0)
			return [];

		List<string> lines = [.. text.Replace("\r\n", "\n").Split('\n')];

		// "a\nb\n" splits to ["a", "b", ""]; that trailing empty is the final newline, not a line.
		if (text.EndsWith('\n') && lines.Count > 0 && lines[^1].Length == 0)
			lines.RemoveAt(lines.Count - 1);

		return lines;
	}

	private static int FindBlock(List<string> lines, List<string> block, int hint)
	{
		if (block.Count == 0)
			return Math.Clamp(hint, 0, lines.Count);

		int maxStart = lines.Count - block.Count;
		if (maxStart < 0)
			return -1;

		// The header's line number may be stale or wildly off for a short file, so anchor the search
		// inside the file's valid range; the loop then reaches every candidate position from there.
		hint = Math.Clamp(hint, 0, maxStart);

		for (int distance = 0; distance <= lines.Count; distance++)
		{
			int forward = hint + distance;
			if (forward >= 0 && forward <= maxStart && MatchesAt(lines, block, forward))
				return forward;

			int backward = hint - distance;
			if (distance > 0 && backward >= 0 && backward <= maxStart && MatchesAt(lines, block, backward))
				return backward;
		}

		return -1;
	}

	private static (int Index, int MatchCount) FindUniqueBlock(List<string> lines, List<string> block)
	{
		int maxStart = lines.Count - block.Count;
		if (maxStart < 0)
			return (-1, 0);

		int first = -1;
		int count = 0;
		for (int start = 0; start <= maxStart; start++)
		{
			if (!MatchesAt(lines, block, start))
				continue;

			if (first < 0)
				first = start;
			count++;
		}

		return (first, count);
	}

	private static bool MatchesAt(List<string> lines, List<string> block, int start)
	{
		for (int k = 0; k < block.Count; k++)
		{
			if (!string.Equals(lines[start + k], block[k], StringComparison.Ordinal))
				return false;
		}

		return true;
	}
}
