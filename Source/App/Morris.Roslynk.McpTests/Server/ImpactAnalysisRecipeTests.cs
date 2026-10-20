using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Morris.Roslynk;
using static Morris.Roslynk.McpTests.Server.PublishedSchema;

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

	[Fact]
	public void WhenTheServerInstructionsNameAParameter_ThenThatNameExistsOnAPublishedTool()
	{
		// Issue #120: the instructions once said "documentPath accepts .cs/.razor/.cshtml", while
		// get_diagnostics takes filePath. An agent followed the prose, sent documentPath, and the SDK
		// silently dropped it, so the call returned counts for the whole solution. Every camelCase token
		// in the instructions is now held to one of three things: a parameter some tool publishes, a tool
		// name, or a result field the output protocol defines. Anything else is a name a caller could
		// send, so it is a drift the wrapper would reject as UnknownArgument.
		HashSet<string> parameters = Tools
			.SelectMany(tool => Properties(tool.ProtocolTool.InputSchema).Keys)
			.ToHashSet(StringComparer.Ordinal);
		HashSet<string> toolNames = Tools
			.Select(tool => tool.ProtocolTool.Name)
			.ToHashSet(StringComparer.Ordinal);

		string instructions = ServerInstructions();

		var unexplained = new List<string>();
		foreach (Match match in Regex.Matches(instructions, @"\b[a-z][A-Za-z0-9]*\b"))
		{
			string token = match.Value;
			// Only camelCase tokens are name-like: they are the shape of every Roslynk parameter and
			// result field. Lowercase words are prose, and PascalCase words are type/symbol examples.
			if (!Regex.IsMatch(token, "^[a-z]+[A-Z]"))
				continue;
			if (parameters.Contains(token) || toolNames.Contains(token) || ResultFields.Contains(token))
				continue;
			unexplained.Add(token);
		}

		Assert.Empty(unexplained.Distinct(StringComparer.Ordinal));
	}

	[Fact]
	public void WhenAToolDescriptionNamesAParameter_ThenThatParameterBelongsToTheToolOrIsAField()
	{
		// The same drift in the per-tool descriptions: a parameter-shaped token a tool's own description
		// mentions must be one of that tool's parameters, a name of another published tool (a description
		// may say "pass actionId to apply_code_action"), or a result header/leaf field.
		HashSet<string> toolNames = Tools
			.Select(tool => tool.ProtocolTool.Name)
			.ToHashSet(StringComparer.Ordinal);

		var unexplained = new List<string>();
		foreach (McpServerTool tool in Tools)
		{
			HashSet<string> own = Properties(tool.ProtocolTool.InputSchema).Keys.ToHashSet(StringComparer.Ordinal);
			foreach (Match match in Regex.Matches(tool.ProtocolTool.Description!, @"\b[a-z][A-Za-z0-9]*\b"))
			{
				string token = match.Value;
				if (!Regex.IsMatch(token, "^[a-z]+[A-Z]"))
					continue;
				if (own.Contains(token) || toolNames.Contains(token) || ResultFields.Contains(token))
					continue;
				unexplained.Add($"{tool.ProtocolTool.Name}:{token}");
			}
		}

		Assert.Empty(unexplained.Distinct(StringComparer.Ordinal));
	}

	/// <summary>
	/// Result names the output protocol defines: headers and outline leaf fields a tool description may
	/// legitimately name without them being parameters. Kept as an explicit list so a new result field is
	/// a deliberate addition rather than a hole the guard silently accepts.
	/// </summary>
	private static readonly HashSet<string> ResultFields = new(StringComparer.Ordinal)
	{
		// Output protocol headers
		"errorMessage", "fullOutputChars", "outputTruncated", "partialItem", "nextStartItem",
		// Resolution and outline leaves
		"resolvedSymbol", "resolvedMethod", "resolvedType", "fullName", "candidates", "symbolKind",
		"resolvedPath",
		"memberKind", "memberName", "typeKind", "typeName", "parameterTypes", "accessKind", "exprKind",
		"symbolLoc", "symbolPath", "flowState", "convertedType", "candidateReason",
		// Per-tool result headers and leaf fields
		"actionId", "diagnosticId", "documentVersion", "updatedCallSites", "unchangedRelated", "renamedMembers",
		"removedCount", "deadConditionals", "omittedParts", "loadDiagnostic", "loadDiagnostics",
		"loadDiagnosticsTruncated", "watchDiagnostic", "watchDiagnosticsTruncated", "documentCount", "projectPath", "solutionId", "startItem", "maxSize",
		"checkOnly", "baseVersions", "expectSnapshot",
		// extract_method emits symbolName as a header: the extracted member's usable signature.
		"symbolName",
	};

	/// <summary>The instructions the real host publishes, read from the registration that owns them.</summary>
	private static string ServerInstructions()
	{
		Type registration = typeof(Morris.Roslynk.Mcp.Hosting.StdioBridge).Assembly
			.GetType("Morris.Roslynk.Mcp.McpServerRegistration", throwOnError: true)!;
		var options = new McpServerOptions();
		registration.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!
			.Invoke(null, [options]);
		return options.ServerInstructions!;
	}
}
