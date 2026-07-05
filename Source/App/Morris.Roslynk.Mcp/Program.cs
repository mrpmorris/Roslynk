using Morris.Roslynk;
using Morris.Roslynk.Infrastructure.Observability;
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

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Safe on every OS: AddWindowsService only takes effect when the process is launched by the
// Windows Service Control Manager; otherwise it registers nothing.
builder.Services.AddWindowsService(options => options.ServiceName = "Roslynk");

builder.AddLoopbackOnlyKestrel();
builder.AddRoslynkObservability();

builder.Services.AddRoslynk();
builder.Services.AddHostedService<IdleEvictionService>();

builder.Services
	.AddMcpServer(McpServerRegistration.Configure)
	.WithHttpTransport()
	.WithToolsFromAssembly(typeof(ServicesRegistration).Assembly);

WebApplication app = builder.Build();

// Eagerly create the metrics publisher so its observable instrument is registered for the process
// lifetime; without a resolve the singleton would never be constructed and the metric never appear.
app.Services.GetRequiredService<SolutionMetrics>();

app.MapMcp();

app.Run();
