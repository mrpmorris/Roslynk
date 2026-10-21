namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Binds Kestrel to loopback only. The MCP server is reachable from the local machine and nowhere
/// else; there is deliberately no option to expose it on an external interface.
/// </summary>
public static class LoopbackOnlyExtensions
{
	public const int DefaultPort = 6502;

	/// <summary>
	/// Binds the host to loopback on <paramref name="port"/>, or on the configured 'Roslynk:Port' when
	/// no port is supplied. The daemon passes <see cref="RoslynkSettings.EffectivePort"/> so the bound
	/// port and the port the bridge dials come from one object.
	/// </summary>
	public static WebApplicationBuilder AddLoopbackOnlyKestrel(this WebApplicationBuilder builder, int? port = null)
	{
		int resolvedPort = port ?? builder.Configuration.GetValue("Roslynk:Port", DefaultPort);
		builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(resolvedPort));
		return builder;
	}
}
