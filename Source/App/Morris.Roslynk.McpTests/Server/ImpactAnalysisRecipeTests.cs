using System.Text.Json;
using ModelContextProtocol.Server;
using Morris.Roslynk;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// The impact-analysis recipe documented in the roslynk skill (issue #22): get_symbol + find_references +
/// get_callers + get_callees (issue #58, the inverse of get_callers) + find_implementations +
/// get_type_hierarchy as one multi_query batch. multi_query rejects unknown argument keys (error=Invalid
/// naming the key), so a recipe argument name that drifts from a tool's published single-call signature is
/// a broken recipe - these tests pin the recipe at the schema level, independent of any fixture solution.
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

		// Exactly the recipe from the skill's "Impact analysis / find usages" section. Every tool that
		// identifies a symbol takes it as symbolName, so one recipe shape works for all of them.
		string[] recipe =
		[
			"get_symbol",
			"find_references",
			"get_callers",
			"get_callees",
			"find_implementations",
			"get_type_hierarchy",
		];

		foreach (string tool in recipe)
		{
			Assert.True(schemasByName.TryGetValue(tool, out JsonElement schema), $"Recipe tool '{tool}' is not published.");
			bool hasArgument = schema.TryGetProperty("properties", out JsonElement properties)
				&& properties.TryGetProperty("symbolName", out _);
			Assert.True(hasArgument, $"Recipe tool '{tool}' does not take 'symbolName'.");
		}
	}

	[Fact]
	public void WhenEverySymbolToolIsPublished_ThenItNamesTheSymbolAsSymbolName()
	{
		// One name per concept: a caller that learned symbolName from get_symbol can reuse it everywhere.
		string[] symbolTools =
		[
			"get_symbol", "get_symbol_body", "get_members", "find_references", "find_reads", "find_writes",
			"get_callers", "get_callees", "find_implementations", "get_type_hierarchy", "rename_symbol",
			"rename_parameter", "change_signature",
		];

		foreach (string tool in symbolTools)
		{
			JsonElement schema = Tools.Single(candidate => candidate.ProtocolTool.Name == tool).ProtocolTool.InputSchema;
			Assert.True(schema.GetProperty("properties").TryGetProperty("symbolName", out _), $"'{tool}' does not take 'symbolName'.");
			foreach (string stale in (string[])["typeName", "memberName", "methodName", "methodId"])
				Assert.False(schema.GetProperty("properties").TryGetProperty(stale, out _), $"'{tool}' still publishes '{stale}'.");
		}
	}

	[Fact]
	public void WhenEveryFileToolIsPublished_ThenItNamesTheFileAsFilePath()
	{
		string[] fileTools =
		[
			"find_definition", "get_expression_info", "get_code_actions", "apply_code_fix", "extract_method",
			"remove_unused_usings", "get_diagnostics",
		];

		foreach (string tool in fileTools)
		{
			JsonElement schema = Tools.Single(candidate => candidate.ProtocolTool.Name == tool).ProtocolTool.InputSchema;
			Assert.True(schema.GetProperty("properties").TryGetProperty("filePath", out _), $"'{tool}' does not take 'filePath'.");
			Assert.False(schema.GetProperty("properties").TryGetProperty("documentPath", out _), $"'{tool}' still publishes 'documentPath'.");
		}
	}

	[Fact]
	public void WhenEveryPagingToolIsPublished_ThenItNamesItsPageAsStartItemAndMaxSize()
	{
		foreach (McpServerTool tool in Tools)
		{
			JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
			if (!properties.TryGetProperty("startItem", out _))
				continue;

			Assert.True(properties.TryGetProperty("maxSize", out _), $"'{tool.ProtocolTool.Name}' pages but publishes no 'maxSize'.");
			Assert.False(properties.TryGetProperty("maxResults", out _), $"'{tool.ProtocolTool.Name}' still publishes 'maxResults'.");
		}
	}

	[Fact]
	public void WhenNoToolIsPublished_ThenNoToolPublishesMaxResults()
	{
		// maxResults merged into maxSize: one paging shape everywhere, with nothing dropped.
		foreach (McpServerTool tool in Tools)
			Assert.False(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("maxResults", out _), $"'{tool.ProtocolTool.Name}' still publishes 'maxResults'.");
	}
}
