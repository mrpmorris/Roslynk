using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting.LoopbackOnlyTests;

public class DefaultPortTests
{
	[Fact]
	public void WhenNoPortIsConfigured_ThenTheDefaultLoopbackPortIs5099()
	{
		Assert.Equal(5099, LoopbackOnlyExtensions.DefaultPort);
	}
}
