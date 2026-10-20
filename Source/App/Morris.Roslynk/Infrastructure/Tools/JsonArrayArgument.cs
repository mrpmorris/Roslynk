using System.Text.Json;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// One tolerant reader for array-typed arguments. Agents commonly serialize an array argument as a JSON
/// string ("[\"Method\"]") even though the published schema says array; the SDK then either fails the
/// binding or, worse, a reader that treats any string as one element silently changes the filter's
/// meaning. A real JSON array is passed through, a JSON-array string is parsed into one, null stays null,
/// and anything else is refused naming the parameter and the accepted form - never dropped.
/// </summary>
internal static class JsonArrayArgument
{
	/// <summary>
	/// Returns the value to bind: the array itself, the array parsed from a JSON-array string, or null.
	/// </summary>
	/// <exception cref="ArgumentException">The value is neither an array, a JSON-array string, nor null.
	/// Mapped to error=Invalid by both entry points.</exception>
	public static JsonElement Normalize(JsonElement value, string toolName, string parameterName)
	{
		if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Null)
			return value;

		if (value.ValueKind == JsonValueKind.String)
		{
			string? text = value.GetString();
			if (!string.IsNullOrWhiteSpace(text))
			{
				try
				{
					using JsonDocument document = JsonDocument.Parse(text);
					if (document.RootElement.ValueKind == JsonValueKind.Array)
						return document.RootElement.Clone();
				}
				catch (JsonException)
				{
					// Falls through to the refusal below, which names the parameter and the accepted form.
				}
			}
		}

		throw new ArgumentException(
			$"Parameter '{parameterName}' of '{toolName}' expects a JSON array, e.g. [\"Method\"]. " +
			$"A JSON-array string such as \"[\\\"Method\\\"]\" is also accepted; received {Describe(value)}.");
	}

	/// <summary>
	/// The strings of an array-typed argument, using the same tolerance as <see cref="Normalize"/>.
	/// Null (or an omitted value) means "no filter"; a non-string element is a caller error.
	/// </summary>
	/// <exception cref="ArgumentException">The value is not an array of strings.</exception>
	public static string[]? Strings(JsonElement value, string toolName, string parameterName)
	{
		JsonElement array = Normalize(value, toolName, parameterName);
		if (array.ValueKind == JsonValueKind.Null)
			return null;

		var items = new List<string>();
		foreach (JsonElement item in array.EnumerateArray())
		{
			if (item.ValueKind != JsonValueKind.String)
			{
				throw new ArgumentException(
					$"Parameter '{parameterName}' of '{toolName}' expects an array of strings, e.g. [\"Method\"]; received {Describe(value)}.");
			}

			items.Add(item.GetString()!);
		}

		return items.ToArray();
	}

	private static string Describe(JsonElement value)
	{
		const int MaximumLength = 200;
		string raw = value.GetRawText();
		return raw.Length <= MaximumLength ? raw : raw[..MaximumLength] + "...";
	}
}
