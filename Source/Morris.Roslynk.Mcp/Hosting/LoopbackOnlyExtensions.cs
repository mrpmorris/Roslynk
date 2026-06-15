namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Binds Kestrel to loopback only. The MCP server is reachable from the local machine and nowhere
/// else; there is deliberately no option to expose it on an external interface.
/// </summary>
public static class LoopbackOnlyExtensions
{
	public const int DefaultPort = 6502;

	public static WebApplicationBuilder AddLoopbackOnlyKestrel(this WebApplicationBuilder builder)
	{
		int port = builder.Configuration.GetValue("Roslynk:Port", DefaultPort);
		builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
		return builder;
	}
}
