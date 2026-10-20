namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The daemon's handshake endpoints. <c>GET /version</c> lets a stdio bridge learn which version owns
/// the port before it pipes a session to it; <c>POST /shutdown</c> lets that bridge retire a daemon of
/// another version so it can start its own. Both are reachable on loopback only, like the rest of the
/// host, and neither is part of the MCP surface.
/// </summary>
public static class DaemonEndpoints
{
	public const string VersionPath = "/version";
	public const string ShutdownPath = "/shutdown";

	/// <summary>
	/// The query parameter naming the version of the bridge requesting shutdown. The daemon refuses the
	/// request when that version matches its own: a concurrent bridge may already have replaced the
	/// mismatched daemon, and shutting the replacement down would start an endless swap.
	/// </summary>
	public const string ClientVersionParameter = "clientVersion";

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

		return app;
	}
}
