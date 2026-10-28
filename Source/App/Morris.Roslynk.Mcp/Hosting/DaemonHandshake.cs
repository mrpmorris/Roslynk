namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The outcome of asking a daemon to stand down. <see cref="AlreadyMatching"/> means the daemon refused
/// because its version already equals the requesting client's (a concurrent bridge replaced it first);
/// <see cref="Unsupported"/> means it does not expose the shutdown endpoint at all.
/// </summary>
public enum DaemonShutdownRequest
{
	Accepted,
	AlreadyMatching,
	Unsupported,
	Unreachable,
}

/// <summary>
/// The outcome of asking a daemon to restart. <see cref="AlreadyInProgress"/> means the daemon refused
/// because a restart of it was already accepted; <see cref="Unsupported"/> means it does not expose the
/// restart endpoint at all (an older daemon).
/// </summary>
public enum DaemonRestartRequest
{
	Accepted,
	Unsupported,
	AlreadyInProgress,
	Unreachable,
}

/// <summary>
/// The daemon operations the version handshake needs. Production uses <see cref="HttpDaemonControl"/>;
/// tests supply controlled fakes so every wait is driven by an observable signal.
/// </summary>
public interface IDaemonControl
{
	bool IsListening(int port);

	/// <summary>Returns the daemon's version, or null when it is not answering the version endpoint yet.</summary>
	Task<string?> TryGetVersionAsync(int port, CancellationToken cancellationToken);

	Task<DaemonShutdownRequest> TryRequestShutdownAsync(int port, string clientVersion, CancellationToken cancellationToken);

	/// <summary>
	/// Asks the daemon to restart without a version guard. Unlike <c>/shutdown</c>, this is allowed to
	/// stop a daemon whose version already matches the caller's.
	/// </summary>
	Task<DaemonRestartRequest> TryRequestRestartAsync(int port, CancellationToken cancellationToken);

	void StartDaemon();
}

/// <summary>
/// Tuning for the handshake. The defaults match the launcher's real budgets; tests shrink them so a
/// failure is observed promptly rather than after a fixed wall-clock guess.
/// </summary>
public sealed class DaemonHandshakeOptions
{
	public required int Port { get; init; }

	public required string ExpectedVersion { get; init; }

	public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

	public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);

	public TimeSpan RestartTimeout { get; init; } = TimeSpan.FromSeconds(10);

	public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

	/// <summary>
	/// Restart even a daemon whose version matches. Defaults to false: a healthy matching daemon holds
	/// warm workspaces that exist precisely to be shared across bridge launches, so recycling it on
	/// every launch would throw that away for no gain.
	/// </summary>
	public bool ForceRestart { get; init; }
}

/// <summary>
/// The Gradle-style version handshake: a bridge reuses a daemon only when that daemon reports the same
/// version as the bridge. A daemon of another version is asked to restart (or, when it has no restart
/// endpoint, to shut down), the bridge waits for the port to free, then starts its own; if the old
/// daemon will not go away the handshake fails with a message naming both versions. A matching daemon
/// is reused unless <see cref="DaemonHandshakeOptions.ForceRestart"/> asks for a fresh process. A
/// listener that has not answered the version endpoint yet is treated as mid-startup and given the
/// startup window, never assumed to be a wrong version.
/// </summary>
public static class DaemonHandshake
{
	/// <summary>
	/// Reuses a development daemon (0.0.1-local, ignoring case and commit metadata), even when a
	/// restart is forced. Other versions follow the normal version replacement and restart handshake.
	/// </summary>
	public static async Task EnsureRunningAsync(
		DaemonHandshakeOptions options,
		IDaemonControl control,
		CancellationToken cancellationToken)
	{
		string? version = await control.TryGetVersionAsync(options.Port, cancellationToken).ConfigureAwait(false);

		if (version is null)
		{
			if (!control.IsListening(options.Port))
			{
				await StartAndConfirmAsync(options, control, cancellationToken).ConfigureAwait(false);
				return;
			}

			// Something holds the port but is not serving the version endpoint yet. It may be a daemon
			// that is still starting, so wait for its version instead of declaring it mismatched.
			version = await WaitForVersionAsync(options, control, options.StartupTimeout, cancellationToken).ConfigureAwait(false)
				?? throw new InvalidOperationException(
					$"A process is listening on port {options.Port} but did not answer the Roslynk version endpoint " +
					$"within {options.StartupTimeout.TotalSeconds:0}s, so the daemon's version is unknown. " +
					$"This client is version {options.ExpectedVersion}; stop the process holding the port and retry.");
		}

		// A local daemon belongs to the developer, including when a startup setting forces a restart.
		if (DaemonVersion.IsDevelopment(version))
			return;

		if (string.Equals(version, options.ExpectedVersion, StringComparison.Ordinal))
		{
			if (!options.ForceRestart)
				return;

			// The daemon matches but the caller asked for a fresh process (a startup-only setting
			// changed). /restart has no version guard, so this is allowed where /shutdown refuses.
			DaemonRestartRequest restart = await control
				.TryRequestRestartAsync(options.Port, cancellationToken)
				.ConfigureAwait(false);

			if (restart is DaemonRestartRequest.Accepted or DaemonRestartRequest.AlreadyInProgress)
			{
				if (await WaitForPortToFreeAsync(options, control, options.RestartTimeout, cancellationToken).ConfigureAwait(false))
				{
					await StartAndConfirmAsync(options, control, cancellationToken).ConfigureAwait(false);
					return;
				}

				throw new InvalidOperationException(
					$"The Roslynk daemon on port {options.Port} is version {version} and was asked to restart, but the port " +
					$"did not free within {options.RestartTimeout.TotalSeconds:0}s. This client is version " +
					$"{options.ExpectedVersion}; stop the process holding the port and retry.");
			}

			// Unsupported (an older daemon with no /restart) or unreachable: reuse the running daemon
			// rather than failing, which is what this handshake did before /restart existed.
			return;
		}

		// A mismatched daemon is replaced gracefully when it supports /restart; /shutdown is the
		// fallback for older daemons and for any restart request that did not go through.
		DaemonRestartRequest restartRequest = await control
			.TryRequestRestartAsync(options.Port, cancellationToken)
			.ConfigureAwait(false);

		if (restartRequest == DaemonRestartRequest.Accepted)
		{
			(bool restartPortFreed, string? restartRemaining) = await WaitForShutdownAsync(
				options,
				control,
				options.RestartTimeout,
				cancellationToken).ConfigureAwait(false);

			if (restartPortFreed)
			{
				await StartAndConfirmAsync(options, control, cancellationToken).ConfigureAwait(false);
				return;
			}

			if (DaemonVersion.IsDevelopment(restartRemaining) ||
				string.Equals(restartRemaining, options.ExpectedVersion, StringComparison.Ordinal))
				return;

			throw new InvalidOperationException(
				$"The Roslynk daemon on port {options.Port} is version {restartRemaining ?? "(unknown)"}, but this client is version " +
				$"{options.ExpectedVersion}, and the daemon did not restart within {options.RestartTimeout.TotalSeconds:0}s. " +
				$"Stop the process holding the port and retry.");
		}

		DaemonShutdownRequest request = await control
			.TryRequestShutdownAsync(options.Port, options.ExpectedVersion, cancellationToken)
			.ConfigureAwait(false);

		if (request == DaemonShutdownRequest.Unsupported)
		{
			throw new InvalidOperationException(
				$"The Roslynk daemon on port {options.Port} is version {version}, but this client is version " +
				$"{options.ExpectedVersion}, and that daemon does not support remote shutdown. Stop it and retry.");
		}

		// Wait for the mismatched daemon to go away, or for a concurrent bridge to have replaced it with
		// one of the expected version already.
		(bool portFreed, string? remaining) = await WaitForShutdownAsync(options, control, options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);

		if (portFreed)
		{
			await StartAndConfirmAsync(options, control, cancellationToken).ConfigureAwait(false);
			return;
		}

		if (DaemonVersion.IsDevelopment(remaining) ||
			string.Equals(remaining, options.ExpectedVersion, StringComparison.Ordinal))
			return;

		throw new InvalidOperationException(
			$"The Roslynk daemon on port {options.Port} is version {remaining ?? "(unknown)"}, but this client is version " +
			$"{options.ExpectedVersion}, and the daemon did not shut down within {options.ShutdownTimeout.TotalSeconds:0}s. " +
			$"Stop the process holding the port and retry.");
	}

	private static async Task StartAndConfirmAsync(
		DaemonHandshakeOptions options,
		IDaemonControl control,
		CancellationToken cancellationToken)
	{
		control.StartDaemon();

		string? version = await WaitForVersionAsync(options, control, options.StartupTimeout, cancellationToken).ConfigureAwait(false);

		if (version is null)
		{
			throw new TimeoutException(
				$"The Roslynk daemon did not start listening on port {options.Port} within " +
				$"{options.StartupTimeout.TotalSeconds:0}s; this client is version {options.ExpectedVersion}. " +
				$"See '{DaemonLauncher.LogFilePath}' for its output.");
		}

		if (!DaemonVersion.IsDevelopment(version) &&
			!string.Equals(version, options.ExpectedVersion, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				$"A Roslynk daemon of version {version} answered on port {options.Port}, but this client is version " +
				$"{options.ExpectedVersion}; another client started a daemon of its own version first.");
		}
	}

	/// <summary>Polls until a version is reported; null when the window expires first.</summary>
	private static async Task<string?> WaitForVersionAsync(
		DaemonHandshakeOptions options,
		IDaemonControl control,
		TimeSpan window,
		CancellationToken cancellationToken)
	{
		using var expiry = new CancellationTokenSource(window);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);

		while (true)
		{
			string? version = await TryGetVersionAsync(options.Port, control, linked.Token, expiry, cancellationToken).ConfigureAwait(false);
			if (version is not null)
				return version;

			if (expiry.IsCancellationRequested)
				return null;

			if (!await DelayAsync(options.PollInterval, linked.Token, expiry, cancellationToken).ConfigureAwait(false))
				return null;
		}
	}

	/// <summary>
	/// Polls until the port is free. Unlike <see cref="WaitForShutdownAsync"/>, a daemon that still
	/// reports the expected version does not count as success: when a matching daemon restarts it keeps
	/// answering until it exits, so only a freed port proves the restart happened.
	/// </summary>
	private static async Task<bool> WaitForPortToFreeAsync(
		DaemonHandshakeOptions options,
		IDaemonControl control,
		TimeSpan window,
		CancellationToken cancellationToken)
	{
		using var expiry = new CancellationTokenSource(window);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);

		while (true)
		{
			string? version = await TryGetVersionAsync(options.Port, control, linked.Token, expiry, cancellationToken).ConfigureAwait(false);
			if (version is null && !control.IsListening(options.Port))
				return true;

			if (expiry.IsCancellationRequested)
				return false;

			if (!await DelayAsync(options.PollInterval, linked.Token, expiry, cancellationToken).ConfigureAwait(false))
				return false;
		}
	}

	/// <summary>
	/// Polls until the port is free or a daemon of the expected version answers. On expiry reports the
	/// last observed version with the port still held.
	/// </summary>
	private static async Task<(bool PortFreed, string? Version)> WaitForShutdownAsync(
		DaemonHandshakeOptions options,
		IDaemonControl control,
		TimeSpan window,
		CancellationToken cancellationToken)
	{
		using var expiry = new CancellationTokenSource(window);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);

		string? lastVersion = null;
		while (true)
		{
			string? version = await TryGetVersionAsync(options.Port, control, linked.Token, expiry, cancellationToken).ConfigureAwait(false);
			if (version is not null)
			{
				lastVersion = version;
				if (DaemonVersion.IsDevelopment(version) ||
					string.Equals(version, options.ExpectedVersion, StringComparison.Ordinal))
					return (false, version);
			}
			else if (!control.IsListening(options.Port))
			{
				return (true, null);
			}

			if (expiry.IsCancellationRequested)
				return (false, lastVersion);

			if (!await DelayAsync(options.PollInterval, linked.Token, expiry, cancellationToken).ConfigureAwait(false))
				return (false, lastVersion);
		}
	}

	private static async Task<string?> TryGetVersionAsync(
		int port,
		IDaemonControl control,
		CancellationToken linkedToken,
		CancellationTokenSource expiry,
		CancellationToken cancellationToken)
	{
		try
		{
			return await control.TryGetVersionAsync(port, linkedToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && expiry.IsCancellationRequested)
		{
			return null;
		}
	}

	private static async Task<bool> DelayAsync(
		TimeSpan delay,
		CancellationToken linkedToken,
		CancellationTokenSource expiry,
		CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(delay, linkedToken).ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && expiry.IsCancellationRequested)
		{
			return false;
		}
	}
}
