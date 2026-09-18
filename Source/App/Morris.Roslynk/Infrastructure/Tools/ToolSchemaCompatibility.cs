using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>
/// Rewrites a generated tool input schema into the shape every client accepts.
/// </summary>
/// <remarks>
/// A C# optional parameter is emitted by the MCP SDK as a property that is absent from "required" but
/// carries a JSON Schema "default" annotation. Several clients convert the schema to their own validator
/// and treat a property that has a "default" as one that must be present, so omitting it is rejected
/// before the call reaches the server ("expected nonoptional, received undefined"). Moving the keyword
/// into the description keeps the caller informed of the default while leaving nothing for a client to
/// mistranslate, and the text is generated from the real C# default so it cannot drift from the
/// signature.
/// </remarks>
internal static class ToolSchemaCompatibility
{
	private static readonly AIJsonSchemaTransformOptions TransformOptions =
		new() { MoveDefaultKeywordToDescription = true };

	/// <summary>
	/// Returns <paramref name="schema"/> with every "default" keyword folded into the description of the
	/// property that carried it.
	/// </summary>
	public static JsonElement Rewrite(JsonElement schema) =>
		AIJsonUtilities.TransformSchema(schema, TransformOptions);
}
