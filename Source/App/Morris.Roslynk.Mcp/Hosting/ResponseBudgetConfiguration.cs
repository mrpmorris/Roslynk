using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Reads the server-wide response budget from host configuration ('Roslynk:MaxResponseChars', or the
/// 'Roslynk__MaxResponseChars' environment variable through the same provider 'Roslynk:Port' uses) and
/// hands the engine the plain value, so the core project stays free of configuration packages. The daemon
/// reads the value once at startup: changing it requires a daemon restart.
/// </summary>
public static class ResponseBudgetConfiguration
{
	public const string MaxResponseCharsKey = "Roslynk:MaxResponseChars";

	public static ResponseBudget FromConfiguration(IConfiguration configuration) =>
		new(configuration.GetValue(MaxResponseCharsKey, ResponseBudget.DefaultMaxChars));
}
