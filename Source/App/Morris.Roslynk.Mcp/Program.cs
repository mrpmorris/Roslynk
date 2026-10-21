using Morris.Roslynk;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Mcp;
using Morris.Roslynk.Mcp.Hosting;
using Morris.Roslynk.Mcp.Observability;

// `stdio` runs the client-launched bridge instead of the daemon: it starts the daemon if needed
// and pipes one MCP session to it over HTTP.
if (args is ["stdio", ..])
{
	await StdioBridge.RunAsync();
	return;
}

// Root content at the install directory, not the working directory. The host watches its content root
// recursively for appsettings changes, so a daemon started from a home directory would put an inotify
// watch on every directory under it and exhaust the user's watch limit on Linux, leaving the solution
// file watchers with none. DaemonLauncher already starts the daemon here; a manual `roslynk` may not.
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
	Args = args,
	ContentRootPath = AppContext.BaseDirectory,
});

// Safe on every OS: AddWindowsService only takes effect when the process is launched by the
// Windows Service Control Manager; otherwise it registers nothing.
builder.Services.AddWindowsService(options => options.ServiceName = "Roslynk");

// Every Roslynk setting comes from this one object, bound from the 'Roslynk' section and read once here.
// Changing any value requires a daemon restart (there is no IOptionsMonitor).
RoslynkSettings settings = RoslynkSettings.Load();

builder.AddLoopbackOnlyKestrel(settings.EffectivePort);
builder.AddRoslynkObservability();

builder.Services.AddRoslynk(
	new ResponseBudget(settings.EffectiveMaxChars),
	settings.EffectiveLoadWaitTimeout);
builder.Services.AddSingleton(settings);
builder.Services.AddHostedService<IdleEvictionService>();

builder.Services
	.AddMcpServer(McpServerRegistration.Configure)
	.WithHttpTransport()
	.WithRoslynkTools();

WebApplication app = builder.Build();

// Eagerly create the metrics publisher so its observable instrument is registered for the process
// lifetime; without a resolve the singleton would never be constructed and the metric never appear.
app.Services.GetRequiredService<SolutionMetrics>();

app.UseMcpBodyValidation();
app.MapMcp();
app.MapDaemonEndpoints();

app.Run();
