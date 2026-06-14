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
		services.AddMcpServer().WithToolsFromAssembly(typeof(ServicesRegistration).Assembly);
		using ServiceProvider provider = services.BuildServiceProvider();

		HashSet<string> toolNames = provider.GetServices<McpServerTool>()
			.Select(tool => tool.ProtocolTool.Name)
			.ToHashSet(StringComparer.Ordinal);

		foreach (string expected in new[]
		{
			"open_solution",
			"get_diagnostics",
			"find_references",
			"rename_symbol",
			"apply_patch",
			"change_signature",
			"find_dead_code",
			"get_method",
			"remove_unused_usings",
		})
		{
			Assert.Contains(expected, toolNames);
		}

		Assert.True(toolNames.Count >= 20, $"Expected the full tool surface; found {toolNames.Count}.");
	}
}
