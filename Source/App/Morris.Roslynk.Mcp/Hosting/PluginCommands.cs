using System.Text.Json;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The `roslynk plugin` noun-verb subcommands, following the .NET command-line design guidance (a
/// grouping command names an area; subcommands name verbs; options carry parameters, never actions):
/// `plugin add {path}` deploys a plugin and restarts the daemon, `plugin remove {name}` removes one
/// and restarts, `plugin list` reports the loaded plugins of a running daemon.
///
/// Validation happens before the daemon is touched and covers naming and layout only — whether the
/// DLL is a plugin at all is discovered at daemon start. On success the command stops the running
/// daemon (if any) by calling `POST /shutdown?clientVersion=0.0.0.0` — a version no daemon reports,
/// so the endpoint's same-version refusal never fires — releasing the file locks the daemon's
/// AssemblyLoadContexts hold on plugin DLLs, then swaps the deployment, then starts the daemon again
/// from the CLI's own binary. A development daemon (0.0.1-local) refuses remote shutdown by design;
/// the command detects one from `GET /version` and fails without deploying.
/// </summary>
internal static class PluginCommands
{
	private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

	public static async Task<int> RunAsync(string[] args)
	{
		try
		{
			return args switch
			{
				["plugin", "add", var path] => await AddAsync(path),
				["plugin", "remove", var name] => await RemoveAsync(name),
				["plugin", "list"] => await ListAsync(),
				["plugin", "add"] or ["plugin", "remove"] => UsageError($"'roslynk plugin {args[1]}' requires an argument."),
				["plugin", ..] => UsageError($"Unknown plugin verb '{string.Join(' ', args.Skip(1))}'."),
				_ => UsageError("Unknown arguments."),
			};
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			Console.Error.WriteLine($"error={exception.Message.ReplaceLineEndings(" ")}");
			return 1;
		}
	}

	public static string Usage() =>
		"""
		Roslynk commands:

		  roslynk                        Starts the daemon (foreground).
		  roslynk stdio                  Runs the MCP stdio bridge.
		  roslynk plugin add {path}      Validates and deploys a plugin, then restarts the daemon.
		  roslynk plugin remove {name}   Removes a plugin, then restarts the daemon.
		  roslynk plugin list            Lists loaded plugins from the running daemon.
		""";

	private static int UsageError(string message)
	{
		Console.Error.WriteLine(message.ReplaceLineEndings(" "));
		Console.Error.WriteLine();
		Console.Error.WriteLine(Usage());
		return 1;
	}

	private static async Task<int> AddAsync(string path)
	{
		IFileSystem fileSystem = new DefaultFileSystem();
		string pluginsRoot = UserPluginsRoot();

		// Validation happens before the daemon is touched, and covers naming and layout only.
		(string name, string sourceFolder) deployment;
		try
		{
			deployment = PluginDeployment.ValidateAdd(fileSystem, path);
			PluginDeployment.RefuseCaseCollision(fileSystem, pluginsRoot, deployment.name);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"error={exception.Message.ReplaceLineEndings(" ")}");
			return 1;
		}

		if (!await StopRunningDaemonAsync())
			return 1;

		try
		{
			PluginDeployment.Deploy(fileSystem, pluginsRoot, deployment.name, deployment.sourceFolder);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"error=The deployment could not be swapped into place: {exception.Message.ReplaceLineEndings(" ")} Re-running 'roslynk plugin add' recovers.");
			await StartDaemonAsync();
			return 1;
		}

		await StartDaemonAsync();
		Console.WriteLine($"Deployed {deployment.name}.");
		return 0;
	}

	private static async Task<int> RemoveAsync(string name)
	{
		IFileSystem fileSystem = new DefaultFileSystem();
		string pluginsRoot = UserPluginsRoot();

		// The name is resolved against the plugins folder's directories before the daemon is touched,
		// so remove is the recovery path for a broken same-normalized deployment.
		string? folder;
		try
		{
			folder = PluginDeployment.ResolveRemoval(fileSystem, pluginsRoot, name);
		}
		catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"error={exception.Message.ReplaceLineEndings(" ")}");
			return 1;
		}

		if (folder is null)
		{
			Console.WriteLine("Not found");
			return 1;
		}

		if (!await StopRunningDaemonAsync())
			return 1;

		try
		{
			fileSystem.DeleteDirectory(Path.Combine(pluginsRoot, folder));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine($"error=The plugin folder '{folder}' could not be deleted: {exception.Message.ReplaceLineEndings(" ")}");
			await StartDaemonAsync();
			return 1;
		}

		await StartDaemonAsync();
		Console.WriteLine($"Removed {folder}.");
		return 0;
	}

	private static async Task<int> ListAsync()
	{
		int port = RoslynkSettings.Load().EffectivePort;
		await DaemonLauncher.EnsureRunningAsync(port, CancellationToken.None);

		using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		string json = await client.GetStringAsync($"http://127.0.0.1:{port}/plugins");
		List<PluginReport>? plugins = JsonSerializer.Deserialize<List<PluginReport>>(json);
		foreach (PluginReport plugin in plugins ?? [])
			Console.WriteLine($"{plugin.Name} ({plugin.Tools.Count} tools)");

		return 0;
	}

	private static string UserPluginsRoot() =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".roslynk", "plugins");

	/// <summary>
	/// Stops the running daemon, if any, through the shutdown handshake. A development daemon refuses
	/// remote shutdown by design and is detected from `GET /version` before anything is touched; a
	/// stop that does not complete within the handshake's shutdown wait fails the command.
	/// </summary>
	private static async Task<bool> StopRunningDaemonAsync()
	{
		int port = RoslynkSettings.Load().EffectivePort;
		var control = new HttpDaemonControl(() => throw new InvalidOperationException("The daemon is not started here."));

		string? version = await control.TryGetVersionAsync(port, CancellationToken.None);
		if (version is null)
			return true; // Nothing is answering; nothing to stop.

		if (DaemonVersion.IsDevelopment(version))
		{
			Console.Error.WriteLine($"error=A development Roslynk daemon (version {version}) refuses remote shutdown by design. Stop it manually and run the command again.");
			return false;
		}

		DaemonShutdownRequest request = await control.TryRequestShutdownAsync(port, clientVersion: "0.0.0.0", CancellationToken.None);
		if (request is not DaemonShutdownRequest.Accepted)
		{
			Console.Error.WriteLine($"error=The Roslynk daemon on port {port} refused the shutdown request ({request}).");
			return false;
		}

		DateTime deadline = DateTime.UtcNow + ShutdownWait;
		while (DateTime.UtcNow < deadline)
		{
			if (!control.IsListening(port) && await control.TryGetVersionAsync(port, CancellationToken.None) is null)
				return true;
			await Task.Delay(PollInterval);
		}

		Console.Error.WriteLine($"error=The Roslynk daemon on port {port} did not stop within {ShutdownWait.TotalSeconds:0} seconds; nothing was deployed.");
		return false;
	}

	private static Task StartDaemonAsync() =>
		DaemonLauncher.EnsureRunningAsync(RoslynkSettings.Load().EffectivePort, CancellationToken.None);

	private sealed record PluginReport(string Name, IReadOnlyList<string> Tools);
}
