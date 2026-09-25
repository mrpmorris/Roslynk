using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// Builds the published tool set exactly the way the real host does (AddRoslynk +
/// WithRoslynkTools) and owns the service provider for its lifetime. Shared as an IClassFixture:
/// exactly one instance per test class, instantiated by xUnit, disposed after the class's tests
/// complete - no test can ever witness a second instance.
/// </summary>
public sealed class ServerBuilder : IDisposable
{
	private readonly ServiceProvider Provider;

	public ServerBuilder()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		Provider = services.BuildServiceProvider();
		Tools = Provider.GetServices<McpServerTool>().ToList();
	}

	public IReadOnlyList<McpServerTool> Tools { get; }

	public void Dispose() => Provider.Dispose();
}
