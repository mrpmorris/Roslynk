using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Morris.Roslynk;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

/// <summary>
/// Transport-facing tests for the malformed-body diagnosis: the same Kestrel host and streamable-HTTP
/// transport the daemon runs, driven by real HTTP requests.
/// </summary>
public class InvalidJsonBodyTests
{
	[Fact]
	public async Task WhenThePostBodyIsNotValidJson_ThenTheErrorNamesLineOffsetTokenAndHint()
	{
		await using McpTestHost host = await McpTestHost.StartAsync();

		// The '\\T' in this Windows path is exactly the mistake the transport used to discard: the parser
		// knows the offending escape and where it is.
		const string invalid =
			"""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"path":"C:\Tmp"}}""";

		using HttpResponseMessage response = await host.PostJsonAsync(invalid);

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
		Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement root = document.RootElement;
		Assert.Equal(JsonValueKind.Null, root.GetProperty("id").ValueKind);
		JsonElement error = root.GetProperty("error");
		Assert.Equal(-32700, error.GetProperty("code").GetInt32());

		string message = error.GetProperty("message").GetString()!;
		Assert.Contains("line 1", message, StringComparison.Ordinal);
		Assert.Contains("offset 67", message, StringComparison.Ordinal);
		Assert.Contains("'T' is an invalid escapable character", message, StringComparison.Ordinal);
		Assert.Contains("Hint:", message, StringComparison.Ordinal);
		Assert.Contains("forward slashes (C:/Data)", message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WhenThePostBodyIsValidJsonButNotJsonRpc_ThenTheTransportsOwnErrorIsKept()
	{
		await using McpTestHost host = await McpTestHost.StartAsync();

		using HttpResponseMessage response = await host.PostJsonAsync("{}");

		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

		using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		JsonElement error = document.RootElement.GetProperty("error");
		Assert.Equal(-32600, error.GetProperty("code").GetInt32());
		Assert.Contains(
			"did not contain a valid JSON-RPC message",
			error.GetProperty("message").GetString(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task WhenThePostBodyIsAValidInitialize_ThenTheMiddlewareLeavesItForTheTransport()
	{
		await using McpTestHost host = await McpTestHost.StartAsync();

		const string initialize =
			"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}""";

		using HttpResponseMessage response = await host.PostJsonAsync(initialize);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		string body = await response.Content.ReadAsStringAsync();
		Assert.Contains("\"serverInfo\"", body, StringComparison.Ordinal);
		Assert.Contains("\"protocolVersion\"", body, StringComparison.Ordinal);
	}

	private sealed class McpTestHost : IAsyncDisposable
	{
		private readonly WebApplication App;

		private McpTestHost(WebApplication app, int port)
		{
			App = app;
			BaseUri = new Uri($"http://127.0.0.1:{port}");
			Client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(30) };
		}

		public Uri BaseUri { get; }
		public HttpClient Client { get; }

		public static async Task<McpTestHost> StartAsync()
		{
			int port = FreePort();

			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				ContentRootPath = AppContext.BaseDirectory,
				EnvironmentName = Environments.Production,
			});

			builder.Logging.ClearProviders();
			builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));
			builder.Services.AddRoslynk();
			builder.Services.AddMcpServer().WithHttpTransport().WithRoslynkTools();

			WebApplication app = builder.Build();
			app.UseMcpBodyValidation();
			app.MapMcp();

			var host = new McpTestHost(app, port);
			await app.StartAsync();
			return host;
		}

		public Task<HttpResponseMessage> PostJsonAsync(string body)
		{
			var content = new StringContent(body, Encoding.UTF8, "application/json");
			var request = new HttpRequestMessage(HttpMethod.Post, McpBodyValidationExtensions.McpPath) { Content = content };
			request.Headers.Accept.ParseAdd("application/json");
			request.Headers.Accept.ParseAdd("text/event-stream");
			return Client.SendAsync(request);
		}

		public async ValueTask DisposeAsync()
		{
			Client.Dispose();
			await App.DisposeAsync();
		}

		private static int FreePort()
		{
			using var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
			probe.Start();
			return ((IPEndPoint)probe.LocalEndpoint).Port;
		}
	}
}
