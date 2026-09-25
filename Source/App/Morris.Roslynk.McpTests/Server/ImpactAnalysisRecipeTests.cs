using System.Text.Json;
using ModelContextProtocol.Server;
using Morris.Roslynk;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// The impact-analysis recipe documented in the roslynk skill (issue #22): get_symbol +
/// find_references + get_callers + find_implementations + get_type_hierarchy as one multi_query
/// batch. multi_query rejects unknown argument keys (error=Invalid naming the key), so a recipe
/// argument name that drifts from a tool's published single-call signature is a broken recipe - these
/// tests pin the recipe at the schema level, independent of any fixture solution.
/// </summary>
public class ImpactAnalysisRecipeTests : IClassFixture<ServerBuilder>
{
	private readonly IReadOnlyList<McpServerTool> Tools;

	public ImpactAnalysisRecipeTests(ServerBuilder server)
	{
		Tools = server.Tools;
	}

	[Fact]
	public void WhenTheImpactRecipeIsDocumented_ThenEveryArgumentNameIsARealParameterOfItsOperation()
	{
		Dictionary<string, JsonElement> schemasByName = Tools
			.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema, StringComparer.Ordinal);

		// Exactly the recipe from the skill's "Impact analysis / find usages" section. Note the two
		// tools whose argument name is not symbolName: get_callers takes methodName, get_type_hierarchy
		// takes typeName - a uniform symbolName recipe would be rejected by multi_query.
		(string Tool, string Argument)[] recipe =
		[
			("get_symbol", "symbolName"),
			("find_references", "symbolName"),
			("get_callers", "methodName"),
			("find_implementations", "symbolName"),
			("get_type_hierarchy", "typeName"),
		];

		foreach ((string tool, string argument) in recipe)
		{
			Assert.True(schemasByName.TryGetValue(tool, out JsonElement schema), $"Recipe tool '{tool}' is not published.");
			bool hasArgument = schema.TryGetProperty("properties", out JsonElement properties)
				&& properties.TryGetProperty(argument, out _);
			Assert.True(hasArgument, $"Recipe argument '{argument}' is not a parameter of '{tool}'.");
		}
	}
}
