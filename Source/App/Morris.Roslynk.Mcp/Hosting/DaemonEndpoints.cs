namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The daemon's handshake endpoints. <c>GET /version</c> lets a stdio bridge learn which version owns
/// the port before it pipes a session to it; <c>POST /shutdown</c> lets that bridge retire a daemon of
/// another version so it can start its own; <c>POST /restart</c> lets it recycle a daemon of its own
/// version, which <c>/shutdown</c> deliberately refuses. All are reachable on loopback only, like the
/// rest of the host, and none is part of the MCP surface.
/// </summary>
public static class DaemonEndpoints
{
	public const string VersionPath = "/version";
	public const string ShutdownPath = "/shutdown";
	public const string RestartPath = "/restart";

	/// <summary>
	/// The query parameter naming the version of the bridge requesting shutdown. The daemon refuses the
	/// request when that version matches its own: a concurrent bridge may already have replaced the
	/// mismatched daemon, and shutting the replacement down would start an endless swap.
	/// </summary>
	public const string ClientVersionParameter = "clientVersion";

	/// <summary>
	/// Process-wide marker: set once a restart has been accepted, so a second concurrent request is
	/// refused instead of stopping an already stopping host (and reporting success twice).
	/// </summary>
	private static int RestartRequested;

	/// <summary>
	/// Clears <see cref="RestartRequested"/>. The flag is process-wide by design, so a test host started
	/// in the same process must clear it before exercising the endpoint.
	/// </summary>
	internal static void ResetRestartState() => Volatile.Write(ref RestartRequested, 0);

	public static WebApplication MapDaemonEndpoints(this WebApplication app)
	{
		app.MapGet(VersionPath, () => Results.Text(DaemonVersion.Current, "text/plain"));

		app.MapPost(ShutdownPath, (HttpRequest request, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
		{
			string? clientVersion = request.Query[ClientVersionParameter];

			if (string.Equals(clientVersion, DaemonVersion.Current, StringComparison.Ordinal))
			{
				return Results.Conflict(
					$"This Roslynk daemon is already version {DaemonVersion.Current}.");
			}

			// Recorded in the daemon's log file: the issue this handshake fixes was silent, and this
			// line is the only place a replacement is visible from the daemon side.
			loggerFactory
				.CreateLogger(typeof(DaemonEndpoints).FullName!)
				.LogWarning(
					"Shutting down Roslynk daemon version {DaemonVersion} at the request of client version {ClientVersion}.",
					DaemonVersion.Current,
					clientVersion ?? "(unspecified)");

			lifetime.StopApplication();
			return Results.Accepted(
				value: $"Roslynk daemon {DaemonVersion.Current} is shutting down for client version {clientVersion ?? "(unspecified)"}.");
		});

		app.MapPost(RestartPath, (IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) =>
		{
			// No version parameter: /restart is only reachable from a bridge that already saw this
			// daemon's version, so it cannot be used to swap a daemon of another version.
			if (Interlocked.CompareExchange(ref RestartRequested, 1, 0) != 0)
			{
				return Results.Conflict(
					$"This Roslynk daemon ({DaemonVersion.Current}) is already restarting.");
			}

			loggerFactory
				.CreateLogger(typeof(DaemonEndpoints).FullName!)
				.LogWarning(
					"Restarting Roslynk daemon version {DaemonVersion} at the request of a client.",
					DaemonVersion.Current);

			lifetime.StopApplication();
			return Results.Accepted(value: $"Roslynk daemon {DaemonVersion.Current} is restarting.");
		});

		return app;
	}
}
