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
			"rename_symbol",
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
}
