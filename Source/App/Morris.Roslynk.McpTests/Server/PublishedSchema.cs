using System.Text.Json;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// Reads the parts of a published MCP input schema these tests assert on, so the schema-reading shape
/// lives once instead of being copied into every test class that pins a tool's parameters.
/// </summary>
internal static class PublishedSchema
{
	/// <summary>Parameter names the schema publishes, keyed ordinally.</summary>
	public static IReadOnlyDictionary<string, JsonElement> Properties(JsonElement schema) =>
		schema.TryGetProperty("properties", out JsonElement properties)
			? properties.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)
			: new Dictionary<string, JsonElement>(StringComparer.Ordinal);

	/// <summary>The names the schema requires the caller to supply.</summary>
	public static IReadOnlyCollection<string> Required(JsonElement schema) =>
		schema.TryGetProperty("required", out JsonElement required)
			? required.EnumerateArray().Select(value => value.GetString()!).ToArray()
			: [];
}
