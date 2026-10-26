namespace Morris.Roslynk.Tests;

/// <summary>
/// Assertions for the compact outline protocol. A result's status header is not part of its content:
/// the shared registry's snapshot is republished as <c>Building</c> while a concurrent diagnostics
/// build compiles it, and that publication accompanies an otherwise complete (or empty) body.
/// Asserting on the whole string would make a read-only test fail whenever another class happens to be
/// compiling the same fixture.
/// </summary>
internal static class OutlineAssert
{
	/// <summary>Asserts the result carries no outline body: no symbol matched.</summary>
	public static void HasNoSymbols(string result)
	{
		Assert.DoesNotContain("error=", result);

		// Outline leaves are tab-indented; headers are not.
		Assert.DoesNotContain(result.Split('\n'), line => line.StartsWith('	'));
	}

	/// <summary>
	/// Asserts the result's headers are exactly <paramref name="expected"/>, ignoring a status header.
	/// A status is a property of the snapshot the shared registry happens to hold at that moment, not of
	/// what the query produced, so a test that pins the produced output must not also pin the status.
	/// </summary>
	public static void HeadersAre(string expected, string result)
	{
		Assert.Equal(expected, WithoutStatus(result));
	}

	/// <summary><paramref name="result"/> with its <c>status=</c> header removed.</summary>
	public static string WithoutStatus(string result) =>
		string.Join(
			"\n",
			result.Split('\n').Where(line => !line.StartsWith("status=", StringComparison.Ordinal)));
}
