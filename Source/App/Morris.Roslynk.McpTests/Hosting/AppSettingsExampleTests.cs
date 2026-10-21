using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

public class AppSettingsExampleTests
{
	[Fact]
	public async Task WhenTheDaemonStartsWithNoAppSettingsFile_ThenItRunsOnCodeDefaultsAndAnswersTheVersionEndpoint()
	{
		string contentRoot = CreateEmptyDirectory();
		try
		{
			Assert.False(File.Exists(Path.Combine(contentRoot, "appsettings.json")));

			await using TestHost host = await TestHost.StartAsync(contentRoot);

			using HttpResponseMessage response = await host.Client.GetAsync(new Uri(host.BaseUri, DaemonEndpoints.VersionPath));

			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Equal(DaemonVersion.Current, (await response.Content.ReadAsStringAsync()).Trim());

			RoslynkSettings settings = RoslynkSettings.FromConfiguration(host.Configuration);
			Assert.Equal(LoopbackOnlyExtensions.DefaultPort, settings.EffectivePort);
			Assert.Equal(ResponseBudget.DefaultMaxChars, settings.EffectiveMaxChars);
			Assert.Equal(LoadWaitTimeout.Default.Duration, settings.EffectiveLoadWaitTimeout.Duration);
			Assert.Equal(
				TimeSpan.FromMinutes(RoslynkSettings.DefaultIdleEvictionMinutes),
				settings.EffectiveIdleEvictionTimeout);

			// The host treats appsettings.json as optional; it must not create one either.
			Assert.False(File.Exists(Path.Combine(contentRoot, "appsettings.json")));
		}
		finally
		{
			Directory.Delete(contentRoot, recursive: true);
		}
	}

	[Fact]
	public void WhenTheExampleFileIsLoadedAsAppSettings_ThenTheCodeDefaultsApply()
	{
		string examplePath = Path.Combine(
			FindRepositoryRoot(),
			"Source",
			"App",
			"Morris.Roslynk.Mcp",
			"appsettings.json.example");

		Assert.True(File.Exists(examplePath), $"Expected the shipped example file at '{examplePath}'.");

		string directory = CreateEmptyDirectory();
		try
		{
			File.Copy(examplePath, Path.Combine(directory, "appsettings.json"));

			IConfiguration configuration = new ConfigurationBuilder()
				.SetBasePath(directory)
				.AddJsonFile("appsettings.json", optional: false)
				.Build();

			// Every key is commented out, so the section does not exist and the code defaults apply.
			Assert.False(configuration.GetSection("Roslynk").Exists());
			Assert.Null(configuration["Roslynk:Port"]);
			Assert.Null(configuration["Roslynk:MaxResponseChars"]);
			Assert.Null(configuration["Roslynk:LoadWaitSeconds"]);
			Assert.Null(configuration["Roslynk:IdleEvictionMinutes"]);

			Assert.Equal(LoopbackOnlyExtensions.DefaultPort, configuration.GetValue("Roslynk:Port", LoopbackOnlyExtensions.DefaultPort));

			RoslynkSettings settings = RoslynkSettings.FromConfiguration(configuration);
			Assert.Equal(LoopbackOnlyExtensions.DefaultPort, settings.EffectivePort);
			Assert.Equal(ResponseBudget.DefaultMaxChars, settings.EffectiveMaxChars);
			Assert.Equal(LoadWaitTimeout.Default.Duration, settings.EffectiveLoadWaitTimeout.Duration);
			Assert.Equal(
				TimeSpan.FromMinutes(RoslynkSettings.DefaultIdleEvictionMinutes),
				settings.EffectiveIdleEvictionTimeout);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	private static string CreateEmptyDirectory()
	{
		string directory = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		return directory;
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);

		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "Source", "Morris.Roslynk.slnx")))
				return directory.FullName;

			directory = directory.Parent;
		}

		throw new InvalidOperationException($"Could not find the repository root above '{AppContext.BaseDirectory}'.");
	}

	private sealed class TestHost : IAsyncDisposable
	{
		private readonly WebApplication App;

		private TestHost(WebApplication app, IConfiguration configuration, int port)
		{
			App = app;
			Configuration = configuration;
			BaseUri = new Uri($"http://127.0.0.1:{port}");
			Client = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(10) };
		}

		public Uri BaseUri { get; }

		public HttpClient Client { get; }

		public IConfiguration Configuration { get; }

		public static async Task<TestHost> StartAsync(string contentRootPath)
		{
			int port = FreePort();

			WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
			{
				ContentRootPath = contentRootPath,
				EnvironmentName = Environments.Production,
			});

			builder.Logging.ClearProviders();
			builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(port));

			WebApplication app = builder.Build();
			app.MapDaemonEndpoints();

			var host = new TestHost(app, builder.Configuration, port);

			await app.StartAsync();
			return host;
		}

		public async ValueTask DisposeAsync()
		{
			Client.Dispose();
			await App.DisposeAsync();
		}

		private static int FreePort()
		{
			using var probe = new TcpListener(IPAddress.Loopback, 0);
			probe.Start();
			return ((IPEndPoint)probe.LocalEndpoint).Port;
		}
	}
}
