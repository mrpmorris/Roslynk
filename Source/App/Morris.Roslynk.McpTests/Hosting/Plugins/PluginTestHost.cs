using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Mcp;
using Morris.Roslynk.Mcp.Hosting;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.McpTests.Hosting.Plugins;

/// <summary>
/// A daemon composed as Program.cs composes it, with Roslynk's tools and the plugins compiled from the given
/// sources, listening on a free loopback port.
/// </summary>
internal sealed class PluginTestHost : IAsyncDisposable
{
	private readonly WebApplication App;

	private PluginTestHost(WebApplication app, int port, PluginCatalog catalog)
	{
		App = app;
		Catalog = catalog;
		BaseUri = new Uri($"http://127.0.0.1:{port}");
		Client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };
	}

	public Uri BaseUri { get; }

	public HttpClient Client { get; }

	public PluginCatalog Catalog { get; }

	public static async Task<PluginTestHost> StartAsync(params (string Name, string Source)[] plugins)
	{
		string pluginsFolder = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), "plugins");
		foreach ((string name, string source) in plugins)
			Compile(pluginsFolder, name, source);

		PluginCatalog catalog = PluginCatalog.Load(pluginsFolder);
		Assert.Empty(catalog.Errors);

		int port = FreePort();
		WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
		{
			ContentRootPath = AppContext.BaseDirectory,
			EnvironmentName = Environments.Production,
		});
		builder.Logging.ClearProviders();
		builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

		builder.Services.AddRoslynk();
		builder.Services.AddRoslynkPlugins(catalog);
		builder.Services
			.AddMcpServer(McpServerRegistration.Configure)
			.WithHttpTransport(options => options.ConfigureSessionOptions = PluginEndpoints.ConfigureSessionOptionsAsync)
			.WithRoslynkTools();

		WebApplication app = builder.Build();
		app.MapMcp();
		app.MapRoslynkPlugins();
		await app.StartAsync();
		return new PluginTestHost(app, port, catalog);
	}

	/// <summary>An MCP client on Roslynk's own endpoint, or on <paramref name="pluginName"/>'s.</summary>
	public Task<McpClient> ConnectAsync(string? pluginName = null) =>
		McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
		{
			Endpoint = new Uri(BaseUri, pluginName is null ? "/" : PluginEndpoints.PathFor(pluginName)),
			TransportMode = HttpTransportMode.StreamableHttp,
		}));

	/// <summary>The plugin instance of type <paramref name="typeName"/>, from the plugin's own load context.</summary>
	public IRoslynkPlugin Instance(string typeName) =>
		Catalog.Plugins.SelectMany(plugin => plugin.Instances).Single(instance => instance.GetType().Name == typeName);

	public Task StopAsync() => App.StopAsync();

	public async ValueTask DisposeAsync()
	{
		Client.Dispose();
		await App.DisposeAsync();
	}

	private static void Compile(string pluginsFolder, string name, string source)
	{
		string folder = Path.Combine(pluginsFolder, name);
		Directory.CreateDirectory(folder);

		string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
		string[] contract = [typeof(IRoslynkPlugin).Assembly.Location, typeof(McpServerToolTypeAttribute).Assembly.Location];
		IEnumerable<MetadataReference> references = trusted.Concat(contract)
			.DistinctBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
			.Select(path => MetadataReference.CreateFromFile(path));

		CSharpCompilation compilation = CSharpCompilation.Create(
			name,
			[CSharpSyntaxTree.ParseText(source)],
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		EmitResult result = compilation.Emit(Path.Combine(folder, name + ".dll"));
		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
	}

	private static int FreePort()
	{
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}
}
