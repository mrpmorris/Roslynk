using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Microsoft.Extensions.AI;
using ModelContextProtocol;

namespace Morris.Roslynk.Features.MultiQuery;

/// <summary>
/// Restores the published operations item schema. <see cref="MultiQueryOperation"/> carries a lenient
/// converter so a malformed operation can be reported per slot, and System.Text.Json's schema exporter
/// cannot describe a type with a custom converter (it emits an array without an 'items' schema). The item
/// shape is generated from <see cref="PublishedOperation"/>, which mirrors the record's contract exactly
/// but has no converter, so the published schema stays the honest tool/arguments shape callers rely on.
/// </summary>
internal static class MultiQuerySchema
{
	private sealed record PublishedOperation(
		[Description("Read-only tool to run.")]
		MultiQueryOp Tool,
		[Description("Tool's single-call arguments.")]
		IReadOnlyDictionary<string, JsonElement>? Arguments = null);

	/// <summary>Replaces the missing operations item schema in place; a no-op when the shape is absent.</summary>
	public static void RestoreOperationsItem(JsonNode? schema)
	{
		if (schema is not JsonObject root
			|| root["properties"] is not JsonObject properties
			|| properties["operations"] is not JsonObject operations)
			return;

		JsonNode? item = JsonSchemaExporter.GetJsonSchemaAsNode(McpJsonUtilities.DefaultOptions, typeof(PublishedOperation));
		if (item is null)
			return;

		JsonElement itemElement = JsonSerializer.SerializeToElement(item);
		JsonElement transformed = AIJsonUtilities.TransformSchema(
			itemElement,
			new AIJsonSchemaTransformOptions { MoveDefaultKeywordToDescription = true });
		JsonNode? repaired = JsonNode.Parse(transformed.GetRawText());
		if (repaired is not JsonObject itemObject
			|| itemObject["properties"] is not JsonObject itemProperties)
			return;

		// The exporter does not read [Description]; the record's own attributes are the contract, so
		// copy them back with the same 'Default: null' suffix every other published parameter uses.
		// The mirror record is a reference type, so the exporter marks the item nullable and an enum
		// property without a JSON type; the real contract is a non-null object whose tool is a string.
		itemObject["type"] = "object";
		JsonObject toolProperty = itemProperties["tool"]!.AsObject();
		toolProperty["description"] = DescriptionOf(nameof(MultiQueryOperation.Tool));
		toolProperty["type"] = "string";
		itemProperties["arguments"]!.AsObject()["description"] = DescriptionOf(nameof(MultiQueryOperation.Arguments)) + " Default: null";

		operations["items"] = itemObject;
	}

	private static string DescriptionOf(string parameterName) =>
		typeof(MultiQueryOperation)
			.GetConstructors()
			.SelectMany(constructor => constructor.GetParameters())
			.Single(parameter => parameter.Name == parameterName)
			.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
			.OfType<DescriptionAttribute>()
			.Single()
			.Description;
}
