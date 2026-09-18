using System.Text.Json;
using System.Text.Json.Nodes;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// Rewrites a generated tool input schema into the shape every client accepts.
/// </summary>
/// <remarks>
/// A C# optional parameter is emitted by the MCP SDK as a property that is absent from "required" but
/// carries a JSON Schema "default" annotation. Several clients convert the schema to their own validator
/// and treat a property that has a "default" as one that must be present, so omitting it is rejected
/// before the call reaches the server ("expected nonoptional, received undefined"). "default" is a pure
/// annotation - the parameter's real default lives in the C# signature and is documented in the
/// parameter description - so dropping it costs nothing and makes every documented-as-optional parameter
/// genuinely omittable.
/// </remarks>
public static class ToolSchemaCompatibility
{
	/// <summary>Keywords whose value is itself a schema.</summary>
	private static readonly string[] SingleSchemaKeywords =
		["items", "additionalProperties", "not", "contains", "propertyNames", "if", "then", "else"];

	/// <summary>Keywords whose value is an array of schemas.</summary>
	private static readonly string[] SchemaArrayKeywords =
		["allOf", "anyOf", "oneOf", "prefixItems"];

	/// <summary>Keywords whose value is an object mapping names to schemas.</summary>
	private static readonly string[] SchemaMapKeywords =
		["properties", "patternProperties", "$defs", "definitions"];

	/// <summary>
	/// Returns <paramref name="schema"/> with every "default" keyword removed, at any depth.
	/// </summary>
	public static JsonElement Rewrite(JsonElement schema)
	{
		if (JsonNode.Parse(schema.GetRawText()) is not JsonObject root)
			return schema;

		StripDefaults(root);
		return JsonSerializer.Deserialize<JsonElement>(root);
	}

	private static void StripDefaults(JsonObject schema)
	{
		schema.Remove("default");

		foreach (string keyword in SingleSchemaKeywords)
		{
			if (schema[keyword] is JsonObject nested)
				StripDefaults(nested);
		}

		foreach (string keyword in SchemaArrayKeywords)
		{
			if (schema[keyword] is JsonArray array)
			{
				foreach (JsonNode? item in array)
				{
					if (item is JsonObject nested)
						StripDefaults(nested);
				}
			}
		}

		foreach (string keyword in SchemaMapKeywords)
		{
			if (schema[keyword] is JsonObject map)
			{
				foreach (KeyValuePair<string, JsonNode?> entry in map)
				{
					if (entry.Value is JsonObject nested)
						StripDefaults(nested);
				}
			}
		}
	}
}
