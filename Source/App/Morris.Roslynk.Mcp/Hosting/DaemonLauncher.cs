using System.Diagnostics;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Ensures the Roslynk HTTP daemon is listening on the loopback port, starting it detached when it
/// is not (the Gradle-daemon pattern: the first client to arrive boots the shared host). The spawned
/// daemon outlives the launcher so its warm workspaces are shared by later sessions; its console
/// output goes to <see cref="LogFilePath"/>, never to the launcher's stdio channel.
/// </summary>
public static class DaemonLauncher
{
	private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

	public static string LogFilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Roslynk",
		"daemon.log");

	public static async Task EnsureRunningAsync(int port, CancellationToken cancellationToken)
	{
		var options = new DaemonHandshakeOptions
		{
			Port = port,
			ExpectedVersion = DaemonVersion.Current,
			StartupTimeout = StartupTimeout,
			ShutdownTimeout = ShutdownTimeout,
			PollInterval = PollInterval,
		};

		await DaemonHandshake.EnsureRunningAsync(options, new HttpDaemonControl(StartDetached), cancellationToken);
	}

	private static void StartDetached()
	{
		(string executable, string? firstArgument) = DaemonCommand();

		Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);

		// The working directory is the executable's folder so the daemon finds its appsettings.json
		// regardless of where the MCP client launched the bridge from.
		ProcessStartInfo startInfo;
		if (OperatingSystem.IsWindows())
		{
			startInfo = new ProcessStartInfo(executable)
			{
				UseShellExecute = true,
				WindowStyle = ProcessWindowStyle.Hidden,
				WorkingDirectory = AppContext.BaseDirectory,
			};
			if (firstArgument is not null)
				startInfo.ArgumentList.Add(firstArgument);
		}
		else
		{
			// nohup + shell backgrounding reparents the daemon away from the launcher, and the
			// redirect keeps its console output out of the stdio JSON-RPC channel.
			startInfo = new ProcessStartInfo("/bin/sh")
			{
				ArgumentList =
				{
					"-c",
					"""nohup "$0" ${2:+"$2"} >>"$1" 2>&1 </dev/null &""",
					executable,
					LogFilePath,
					firstArgument ?? "",
				},
				UseShellExecute = false,
				WorkingDirectory = AppContext.BaseDirectory,
			};
		}

		// If two bridges race, the loser's daemon fails to bind the port and exits; both bridges
		// then connect to the winner, so the race is benign.
		Process.Start(startInfo);
	}

	/// <summary>
	/// The command that starts the daemon. Launched directly (apphost) the process re-runs itself;
	/// launched as a dotnet tool (dnx / dotnet tool exec) the process is the 'dotnet' host, so the
	/// daemon must be started as 'dotnet <entry-assembly.dll>' instead.
	/// </summary>
	private static (string Executable, string? FirstArgument) DaemonCommand()
	{
		string executable = Environment.ProcessPath
			?? throw new InvalidOperationException("Cannot determine the Roslynk executable path to start the daemon.");

		string processName = Path.GetFileNameWithoutExtension(executable);
		if (!processName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
			return (executable, null);

		string entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location is { Length: > 0 } location
			? location
			: throw new InvalidOperationException("Cannot determine the Roslynk assembly path to start the daemon.");

		return (executable, entryAssembly);
	}
}
