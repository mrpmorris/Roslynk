using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Morris.Roslynk;
using Morris.Roslynk.Features.Help.Help;
using Morris.Roslynk.Infrastructure.Tools;
using static Morris.Roslynk.McpTests.Server.PublishedSchema;

namespace Morris.Roslynk.McpTests.Server;

public class ToolSchemaTests : IClassFixture<ServerBuilder>
{
	private readonly IReadOnlyList<McpServerTool> Tools;

	public ToolSchemaTests(ServerBuilder server)
	{
		Tools = server.Tools;
	}

	[Fact]
	public void WhenAToolIsPublished_ThenItsSchemaCarriesNoDefaultKeyword()
	{
		// A "default" makes several clients treat the property as one that must be present, which is exactly
		// what makes a documented-as-optional parameter unusable.
		foreach (McpServerTool tool in Tools)
		{
			Assert.DoesNotContain(
				"\"default\"",
				tool.ProtocolTool.InputSchema.GetRawText(),
				StringComparison.Ordinal);
		}
	}

	[Fact]
	public void WhenAToolTakesSolutionId_ThenItIsRequiredExceptWhereTheToolScopesAnOptionalLookup()
	{
		// Every data tool needs a solutionId. get_solution_status is the deliberate exception: it
		// reports on the shared daemon itself, so omitting the id must keep listing every loaded
		// solution exactly as before. Any other optional solutionId would change an existing call.
		foreach (McpServerTool tool in Tools)
		{
			JsonElement schema = tool.ProtocolTool.InputSchema;
			if (!Properties(schema).ContainsKey("solutionId"))
				continue;

			if (tool.ProtocolTool.Name == "get_solution_status")
			{
				Assert.DoesNotContain("solutionId", Required(schema));
				continue;
			}

			Assert.True(
				Required(schema).Contains("solutionId"),
				$"{tool.ProtocolTool.Name} takes solutionId but does not require it.");
		}
	}

	[Fact]
	public void WhenGetSolutionStatusIsPublished_ThenSolutionIdIsAnOptionalString()
	{
		McpServerTool tool = Tools.Single(candidate => candidate.ProtocolTool.Name == "get_solution_status");
		JsonElement schema = tool.ProtocolTool.InputSchema;

		Assert.True(Properties(schema).TryGetValue("solutionId", out JsonElement solutionId), "get_solution_status does not publish 'solutionId'.");
		Assert.Equal("string", PrimaryType(solutionId));
		Assert.DoesNotContain("solutionId", Required(schema));
		Assert.Contains("Optional", solutionId.GetProperty("description").GetString()!, StringComparison.Ordinal);
		Assert.Contains("NotFound", McpToolCatalog.Descriptions[tool.ProtocolTool.Name], StringComparison.Ordinal);
	}

	[Fact]
	public void WhenApplyCodeFixIsPublished_ThenItsPositionIsRequired()
	{
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "apply_code_fix").ProtocolTool.InputSchema;

		IReadOnlyCollection<string> required = Required(schema);

		Assert.Contains("line", required);
		Assert.Contains("column", required);
		Assert.DoesNotContain("checkOnly", required);
	}

	[Fact]
	public void WhenSearchSymbolsIsPublished_ThenKindsIsAnOptionalArrayOfStrings()
	{
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "search_symbols").ProtocolTool.InputSchema;
		JsonElement kinds = Properties(schema)["kinds"];

		// A nullable array publishes its type as a union (["array", "null"]) rather than a single string.
		Assert.Contains("array", TypeNames(kinds));
		Assert.Contains("string", TypeNames(kinds.GetProperty("items")));
		Assert.DoesNotContain("kinds", Required(schema));
		Assert.Contains("JSON array", kinds.GetProperty("description").GetString()!, StringComparison.Ordinal);
	}

	[Fact]
	public void WhenSearchSymbolsIsPublished_ThenItsNameFilterIsSymbolNameFilter()
	{
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "search_symbols").ProtocolTool.InputSchema;

		// Issue #152: 'query' read as free-text search, but the parameter only ever matched a symbol's
		// own name, so {query: "objVM", kinds: ["Method"]} could never mean "the methods of objVM".
		Assert.Contains("symbolNameFilter", Required(schema));
		Assert.DoesNotContain("query", Properties(schema).Keys);
	}

	[Fact]
	public void WhenGetDiagnosticsIsPublished_ThenIdsIsAnOptionalArrayOfStrings()
	{
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "get_diagnostics").ProtocolTool.InputSchema;
		JsonElement ids = Properties(schema)["ids"];

		Assert.Contains("array", TypeNames(ids));
		Assert.Contains("string", TypeNames(ids.GetProperty("items")));
		Assert.DoesNotContain("ids", Required(schema));
	}

	/// <summary>The declared type names, whether the schema writes one string or a nullable union.</summary>
	private static string[] TypeNames(JsonElement schema)
	{
		JsonElement type = schema.GetProperty("type");
		return type.ValueKind == JsonValueKind.String
			? [type.GetString()!]
			: [.. type.EnumerateArray().Select(value => value.GetString()!)];
	}

	[Fact]
	public void WhenAToolParameterHasADefaultValue_ThenTheSchemaMarksItOptional()
	{
		Dictionary<string, JsonElement> schemasByToolName = Tools
			.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema, StringComparer.Ordinal);

		foreach ((string toolName, MethodInfo method) in ToolMethods())
		{
			Assert.True(schemasByToolName.TryGetValue(toolName, out JsonElement schema), $"Tool '{toolName}' was not published.");
			IReadOnlyCollection<string> required = Required(schema);
			IReadOnlyDictionary<string, JsonElement> properties = Properties(schema);

			foreach (ParameterInfo parameter in method.GetParameters().Where(parameter => parameter.HasDefaultValue))
			{
				string parameterName = parameter.Name!;

				// A CancellationToken and any injected service are bound by the host, not by the caller, so
				// they never reach the schema.
				if (!properties.ContainsKey(parameterName))
					continue;

				Assert.False(
					required.Contains(parameterName),
					$"{toolName}.{parameterName} has a default value but the schema requires it.");
			}
		}
	}

	[Fact]
	public void WhenEveryToolIsPublished_ThenTheRequiredSetIsExactlyItsParametersWithoutDefaults()
	{
		// This is the call a caller writes when it follows the documentation: the arguments with no default,
		// and nothing else. Anything extra in "required" is a parameter the caller cannot omit.
		Dictionary<string, JsonElement> schemasByToolName = Tools
			.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.InputSchema, StringComparer.Ordinal);

		foreach ((string toolName, MethodInfo method) in ToolMethods())
		{
			Assert.True(schemasByToolName.TryGetValue(toolName, out JsonElement schema), $"Tool '{toolName}' was not published.");
			IReadOnlyDictionary<string, JsonElement> properties = Properties(schema);

			string[] expected = method.GetParameters()
				.Where(parameter => !parameter.HasDefaultValue)
				.Select(parameter => parameter.Name!)
				.Where(properties.ContainsKey)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToArray();

			string[] actual = Required(schema).OrderBy(name => name, StringComparer.Ordinal).ToArray();
			Assert.Equal(expected, actual);
		}
	}

	[Fact]
	public void WhenToolsArePublished_ThenTheyUseShortSummariesAndKeepFullHelp()
	{
		foreach (McpServerTool tool in Tools)
		{
			Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Title), $"Tool '{tool.ProtocolTool.Name}' has no short summary.");
			Assert.Equal(tool.ProtocolTool.Title, tool.ProtocolTool.Description);
			Assert.True(McpToolCatalog.Descriptions.TryGetValue(tool.ProtocolTool.Name, out string? fullDescription));
			Assert.False(string.IsNullOrWhiteSpace(fullDescription), $"Tool '{tool.ProtocolTool.Name}' has no full help description.");
		}
	}

	[Fact]
	public void WhenHelpIsRequested_ThenItReturnsTheFullDescriptionByToolName()
	{
		var help = new HelpTool();

		Assert.Equal(McpToolCatalog.Descriptions["search_symbols"], help.Help("search_symbols"));
		Assert.StartsWith("error=NotFound", help.Help("missing_tool"), StringComparison.Ordinal);
	}

	[Fact]
	public void WhenQueryGuidanceIsPublished_ThenDescriptionsCarryCurrentContracts()
	{
		string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

		string multiQuery = McpToolCatalog.Descriptions["multi_query"];
		Assert.Contains(
			"Operations cannot reference each other's output. Batch only queries needed for the question.",
			Normalize(multiQuery),
			StringComparison.Ordinal);

		string findReferences = McpToolCatalog.Descriptions["find_references"];
		Assert.Contains(
			"Combine with get_callers/get_callees/find_implementations/get_type_hierarchy via multi_query for usage/impact analysis.",
			Normalize(findReferences),
			StringComparison.Ordinal);
	}

	[Fact]
	public void WhenServerIsConfigured_ThenInstructionsCarrySharedLibraryGuidance()
	{
		Type registration = typeof(Morris.Roslynk.Mcp.Hosting.StdioBridge).Assembly
			.GetType("Morris.Roslynk.Mcp.McpServerRegistration", throwOnError: true)!;
		var options = new McpServerOptions();
		registration.GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!
			.Invoke(null, [options]);

		Assert.False(string.IsNullOrWhiteSpace(options.ServerInstructions));
		foreach (string section in new[]
		{
			"Scope", "Lifecycle", "Output protocol", "Parameters", "Name grammar", "Conditional coverage",
			"Failures and freshness", "Paging", "Reads and writes", "Batching", "Editing and diagnostics", "Razor"
		})
			Assert.Contains("## " + section, options.ServerInstructions, StringComparison.Ordinal);

		Assert.Contains("saving failures trigger attempted rollback, which may fail", options.ServerInstructions, StringComparison.Ordinal);
		foreach (McpServerTool tool in Tools)
		{
			Assert.DoesNotContain("## Shared paging protocol", tool.ProtocolTool.Description!, StringComparison.Ordinal);
			Assert.DoesNotContain("## Name grammar", tool.ProtocolTool.Description!, StringComparison.Ordinal);
			Assert.DoesNotContain("## Output protocol", tool.ProtocolTool.Description!, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void WhenGetDiagnosticsIsPublished_ThenItsFiltersAreOptional()
	{
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "get_diagnostics").ProtocolTool.InputSchema;

		IReadOnlyDictionary<string, JsonElement> properties = Properties(schema);
		IReadOnlyCollection<string> required = Required(schema);

		foreach (string parameter in (string[])["projectName", "filePath", "ids", "maxSize", "summaryOnly"])
		{
			Assert.True(properties.ContainsKey(parameter), $"get_diagnostics does not publish '{parameter}'.");
			Assert.DoesNotContain(parameter, required);
		}
	}

	[Fact]
	public void WhenGetDiagnosticsIdsIsPublished_ThenItIsAnArrayOfStrings()
	{
		// ids is the first array-typed parameter on the MCP surface; pin its shape so callers can rely on it.
		// The SDK publishes nullable parameters as a <type>,null union, so read the primary type accordingly.
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "get_diagnostics").ProtocolTool.InputSchema;

		JsonElement ids = Properties(schema)["ids"];
		Assert.Equal("array", PrimaryType(ids));
		Assert.Equal("string", PrimaryType(ids.GetProperty("items")));
	}

	private static string PrimaryType(JsonElement property)
	{
		JsonElement type = property.GetProperty("type");
		return type.ValueKind == JsonValueKind.Array
			? type.EnumerateArray().Select(value => value.GetString()!).Single(value => value != "null")
			: type.GetString()!;
	}

	[Fact]
	public void WhenGetSymbolBodyIsPublished_ThenItsPagingParametersAreOptionalIntegers()
	{
		JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == "get_symbol_body").ProtocolTool.InputSchema;
		IReadOnlyDictionary<string, JsonElement> properties = Properties(schema);
		IReadOnlyCollection<string> required = Required(schema);

		// The paging parameters are plain omittable integers - the MoveDefaultKeywordToDescription
		// transform stripped the schema's default keyword and moved each default into the description.
		foreach (string name in new[] { "startItem", "maxSize", "part" })
		{
			Assert.True(properties.ContainsKey(name), $"{name} is missing from the get_symbol_body schema.");
			Assert.Equal("integer", properties[name].GetProperty("type").GetString());
			Assert.DoesNotContain(name, required);
		}

		Assert.Contains("Default: 0", properties["maxSize"].GetProperty("description").GetString(), StringComparison.Ordinal);
		Assert.Contains("Default: 1", properties["startItem"].GetProperty("description").GetString(), StringComparison.Ordinal);
		Assert.Contains("Default: 0", properties["part"].GetProperty("description").GetString(), StringComparison.Ordinal);
	}

	[Fact]
	public void WhenAPagingToolIsPublished_ThenStartItemAndMaxSizeAreOptionalIntegers()
	{
		// Every tool that pages exposes the same two omittable parameters, so a caller learns the contract
		// once; the schema carries no defaults (the descriptions do).
		foreach (string toolName in new[] { "find_references", "get_callers", "get_callees", "find_implementations", "search_symbols", "get_members", "get_type_hierarchy", "find_dead_code", "find_dead_conditionals", "get_diagnostics" })
		{
			JsonElement schema = Tools.Single(tool => tool.ProtocolTool.Name == toolName).ProtocolTool.InputSchema;
			IReadOnlyDictionary<string, JsonElement> properties = Properties(schema);
			IReadOnlyCollection<string> required = Required(schema);
			foreach (string name in new[] { "startItem", "maxSize" })
			{
				Assert.True(properties.ContainsKey(name), $"{name} is missing from the {toolName} schema.");
				Assert.Equal("integer", properties[name].GetProperty("type").GetString());
				Assert.DoesNotContain(name, required);
			}
		}
	}

	[Fact]
	public void WhenEveryPagingToolIsPublished_ThenItPublishesAnOptionalCountOnlyBoolean()
	{
		foreach (McpServerTool tool in Tools)
		{
			JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
			if (!properties.TryGetProperty("startItem", out _))
				continue;

			Assert.True(
				properties.TryGetProperty("countOnly", out JsonElement countOnly),
				$"'{tool.ProtocolTool.Name}' pages but publishes no 'countOnly'.");
			Assert.Equal("boolean", PrimaryType(countOnly));
			Assert.DoesNotContain("countOnly", Required(tool.ProtocolTool.InputSchema));
		}
	}

	private static IEnumerable<(string ToolName, MethodInfo Method)> ToolMethods()
	{
		foreach (Type type in typeof(ServicesRegistration).Assembly.GetTypes())
		{
			if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null)
				continue;

			foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
			{
				if (method.GetCustomAttribute<McpServerToolAttribute>() is McpServerToolAttribute attribute && attribute.Name is string name)
					yield return (name, method);
			}
		}
	}
}
