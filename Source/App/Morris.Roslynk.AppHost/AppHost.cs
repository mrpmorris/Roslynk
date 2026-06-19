var builder = DistributedApplication.CreateBuilder(args);

// Use an explicit project launch profile so Visual Studio launches the MCP project itself instead of
// falling back to a broken executable-style launch. The MCP host still binds Kestrel to loopback
// http://localhost:6502 itself (see LoopbackOnlyExtensions), so declare that exact unproxied endpoint.
builder.AddProject<Projects.Morris_Roslynk_Mcp>("roslynk-mcp", launchProfileName: "Aspire")
	.WithHttpEndpoint(port: 6502, isProxied: false);

builder.Build().Run();
