using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk;

namespace Morris.Roslynk.McpTests.Server;

public class ToolSchemaTests
{
	[Fact]
	public void WhenAToolIsPublished_ThenItsSchemaCarriesNoDefaultKeyword()
	{
		// A "default" makes several clients treat the property as one that must be present, which is exactly
		// what makes a documented-as-optional parameter unusable.
		foreach (McpServerTool tool in BuildServer())
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
		foreach (McpServerTool tool in BuildServer())
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
		Dictionary<string, JsonElement> schemasByToolName = BuildServer()
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
		Dictionary<string, JsonElement> schemasByToolName = BuildServer()
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

	private static IReadOnlyList<McpServerTool> BuildServer()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		using ServiceProvider provider = services.BuildServiceProvider();
		return provider.GetServices<McpServerTool>().ToList();
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
