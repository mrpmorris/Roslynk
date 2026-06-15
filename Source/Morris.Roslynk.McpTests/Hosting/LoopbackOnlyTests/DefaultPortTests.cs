using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting.LoopbackOnlyTests;

public class DefaultPortTests
{
	[Fact]
	public void WhenNoPortIsConfigured_ThenTheDefaultLoopbackPortIs6502()
	{
		Assert.Equal(6502, LoopbackOnlyExtensions.DefaultPort);
	}
}
