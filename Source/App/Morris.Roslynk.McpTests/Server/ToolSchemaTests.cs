using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Morris.Roslynk;

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
	public void WhenAToolTakesSolutionId_ThenItIsRequired()
	{
		foreach (McpServerTool tool in Tools)
		{
			JsonElement schema = tool.ProtocolTool.InputSchema;
			if (!Properties(schema).ContainsKey("solutionId"))
				continue;

			Assert.True(
				Required(schema).Contains("solutionId"),
				$"{tool.ProtocolTool.Name} takes solutionId but does not require it.");
		}
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
	public void WhenTheImpactAnalysisHintsAreExpected_ThenMultiQueryAndFindReferencesDescriptionsCarryThem()
	{
		// Issue #22: the roslynk skill steers impact/usage questions to multi_query, and the tool
		// descriptions carry the same hint so clients without skill support (and subagents) see it too.
		// Pinning the exact sentences makes a future reword a deliberate decision, not a silent drift.
		Dictionary<string, string> descriptions = Tools
			.ToDictionary(tool => tool.ProtocolTool.Name, tool => tool.ProtocolTool.Description ?? "", StringComparer.Ordinal);

		string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

		Assert.Contains(
			"For usage/impact questions, batch find_references, get_callers, find_implementations and get_type_hierarchy in one call.",
			Normalize(descriptions["multi_query"]),
			StringComparison.Ordinal);
		Assert.Contains(
			"Prefer this over text search for usages in *.cs, *.cshtml and *.razor; combine with get_callers/find_implementations via multi_query for impact analysis.",
			Normalize(descriptions["find_references"]),
			StringComparison.Ordinal);
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

	private static IReadOnlyDictionary<string, JsonElement> Properties(JsonElement schema) =>
		schema.TryGetProperty("properties", out JsonElement properties)
			? properties.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)
			: new Dictionary<string, JsonElement>(StringComparer.Ordinal);

	private static IReadOnlyCollection<string> Required(JsonElement schema) =>
		schema.TryGetProperty("required", out JsonElement required)
			? required.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal)
			: new HashSet<string>(StringComparer.Ordinal);
}
