using Morris.Roslynk;
using Morris.Roslynk.Mcp;
using Morris.Roslynk.Mcp.Hosting;
using Morris.Roslynk.Mcp.Observability;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Roslynk");

builder.AddLoopbackOnlyKestrel();
builder.AddRoslynkObservability();

builder.Services
	.AddRoslynk()
	.AddMcpServer(McpServerRegistration.Configure)
	.WithHttpTransport()
	.WithToolsFromAssembly(typeof(ServicesRegistration).Assembly);

WebApplication app = builder.Build();

app.MapMcp();

app.Run();
