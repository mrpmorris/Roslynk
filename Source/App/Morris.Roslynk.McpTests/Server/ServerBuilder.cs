using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// Builds the published tool set exactly the way the real host does (AddRoslynk + WithRoslynkTools),
/// shared by the schema tests. The provider is disposed after listing; the returned McpServerTool
/// objects are published snapshots and stay valid without it.
/// </summary>
internal static class ServerBuilder
{
	public static IReadOnlyList<McpServerTool> BuildServer()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		using ServiceProvider provider = services.BuildServiceProvider();
		return provider.GetServices<McpServerTool>().ToList();
	}
}
