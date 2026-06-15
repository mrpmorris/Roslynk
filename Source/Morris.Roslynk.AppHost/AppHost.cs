var builder = DistributedApplication.CreateBuilder(args);

// The MCP host binds Kestrel to loopback http://localhost:5099 itself (see LoopbackOnlyExtensions) and
// ignores any injected URLs, so bypass its launch profile and declare that exact unproxied endpoint.
builder.AddProject<Projects.Morris_Roslynk_Mcp>("roslynk-mcp", launchProfileName: null)
	.WithHttpEndpoint(port: 5099, isProxied: false);

builder.Build().Run();
