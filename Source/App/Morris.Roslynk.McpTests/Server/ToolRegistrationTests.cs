using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk;

namespace Morris.Roslynk.McpTests.Server;

public class ToolRegistrationTests
{
	[Fact]
	public void WhenTheServerIsBuiltAsTheHostDoes_ThenTheExpectedToolsAreExposed()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		using ServiceProvider provider = services.BuildServiceProvider();

		HashSet<string> toolNames = provider.GetServices<McpServerTool>()
			.Select(tool => tool.ProtocolTool.Name)
			.ToHashSet(StringComparer.Ordinal);

		foreach (string expected in new[]
		{
			"open_solution",
			"get_diagnostics",
			"get_symbol_body",
			"find_references",
			"find_reads",
			"find_writes",
			"rename_symbol",
			"rename_parameter",
			"apply_patch",
			"change_signature",
			"extract_method",
			"find_dead_code",
			"remove_unused_usings",
			"multi_query",
		})
		{
			Assert.Contains(expected, toolNames);
		}

		Assert.True(toolNames.Count >= 20, $"Expected the full tool surface; found {toolNames.Count}.");
	}

	[Fact]
	public void WhenRenameSymbolIsPublished_ThenOnlyItsUserParametersAreRequiredAndCancellationIsHidden()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		using ServiceProvider provider = services.BuildServiceProvider();

		System.Text.Json.JsonElement schema = provider.GetServices<McpServerTool>()
			.Single(tool => tool.ProtocolTool.Name == "rename_symbol")
			.ProtocolTool.InputSchema;

		string[] required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).Order().ToArray();
		Assert.Equal(new[] { "newName", "solutionId", "symbolName" }, required);
		Assert.False(schema.GetProperty("properties").TryGetProperty("cancellationToken", out _));
	}
}
