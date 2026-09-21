using System.Text.Json;

namespace Morris.Roslynk.Tests.Features.MultiQuery;

/// <summary>Shared helpers for the multi_query test files.</summary>
internal static class MultiQueryTestHelpers
{
	public static IReadOnlyDictionary<string, JsonElement> Args(params (string Key, JsonElement Value)[] pairs)
	{
		var dictionary = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach ((string key, JsonElement value) in pairs)
			dictionary[key] = value;
		return dictionary;
	}

	public static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);
}
