namespace Morris.Roslynk.Infrastructure.Observability;

/// <summary>
/// Shared conventions for the tags Roslynk attaches to its <see cref="RoslynkActivitySource"/> spans and
/// to MCP request spans. A string value is capped at <see cref="MaxValueLength"/> characters so a span
/// never carries an unbounded value (a large patch, a long path) as an attribute.
/// </summary>
public static class ActivityTags
{
	/// <summary>The maximum number of characters of a string value recorded on a span tag.</summary>
	public const int MaxValueLength = 64;

	/// <summary>Returns <paramref name="value"/> capped at <see cref="MaxValueLength"/> characters.</summary>
	public static string? Truncate(string? value) =>
		value is null || value.Length <= MaxValueLength ? value : value[..MaxValueLength];
}
