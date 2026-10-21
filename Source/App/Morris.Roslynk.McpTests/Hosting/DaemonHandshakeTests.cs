using Morris.Roslynk.Mcp.Hosting;

namespace Morris.Roslynk.McpTests.Hosting;

public class DaemonHandshakeTests
{
	private const string ExpectedVersion = "2.1.0";
	private const string OtherVersion = "2.0.0";

	[Fact]
	public async Task WhenTheDaemonVersionMatches_ThenTheRunningDaemonIsReused()
	{
		var daemon = new FakeDaemonControl { Listening = true, Version = ExpectedVersion };

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(0, daemon.StartCount);
		Assert.Equal(0, daemon.ShutdownRequestCount);
		Assert.Equal(0, daemon.RestartRequestCount);
	}

	[Fact]
	public async Task WhenTheDaemonVersionMatchesAndARestartIsForced_ThenItIsRestartedAndStarted()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = ExpectedVersion,
			StartedVersion = ExpectedVersion,
			RestartResult = DaemonRestartRequest.Accepted,
		};

		await DaemonHandshake.EnsureRunningAsync(Options(forceRestart: true), daemon, CancellationToken.None);

		Assert.Equal(1, daemon.RestartRequestCount);
		Assert.Equal(0, daemon.ShutdownRequestCount);
		Assert.Equal(1, daemon.StartCount);
		Assert.Equal(ExpectedVersion, daemon.Version);
	}

	[Fact]
	public async Task WhenTheDaemonVersionDiffersAndRestartIsAccepted_ThenItIsRestartedWithoutShutdown()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = OtherVersion,
			StartedVersion = ExpectedVersion,
			RestartResult = DaemonRestartRequest.Accepted,
		};

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(1, daemon.RestartRequestCount);
		Assert.Equal(0, daemon.ShutdownRequestCount);
		Assert.Equal(1, daemon.StartCount);
		Assert.Equal(ExpectedVersion, daemon.Version);
	}

	[Fact]
	public async Task WhenTheDaemonVersionDiffersAndRestartIsUnsupported_ThenItFallsBackToShutdown()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = OtherVersion,
			StartedVersion = ExpectedVersion,
			RestartResult = DaemonRestartRequest.Unsupported,
			ShutdownResult = DaemonShutdownRequest.Accepted,
		};

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(1, daemon.RestartRequestCount);
		Assert.Equal(1, daemon.ShutdownRequestCount);
		Assert.Equal(ExpectedVersion, daemon.LastClientVersion);
		Assert.Equal(1, daemon.StartCount);
		Assert.Equal(ExpectedVersion, daemon.Version);
	}

	[Fact]
	public async Task WhenTheDaemonIsRestartedButNeverFreesThePort_ThenTheHandshakeFailsNamingTheVersionsAndTimeout()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = ExpectedVersion,
			StartedVersion = ExpectedVersion,
			RestartResult = DaemonRestartRequest.Accepted,
			PortFreesOnRestart = false,
		};

		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => DaemonHandshake.EnsureRunningAsync(Options(forceRestart: true), daemon, CancellationToken.None));

		Assert.Contains(ExpectedVersion, exception.Message);
		Assert.Contains("0s", exception.Message);
		Assert.Equal(0, daemon.StartCount);
	}

	[Fact]
	public async Task WhenTheDaemonVersionDiffers_ThenItIsShutDownAndTheCurrentVersionIsStarted()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = OtherVersion,
			StartedVersion = ExpectedVersion,
			ShutdownResult = DaemonShutdownRequest.Accepted,
		};

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(1, daemon.ShutdownRequestCount);
		Assert.Equal(ExpectedVersion, daemon.LastClientVersion);
		Assert.Equal(1, daemon.StartCount);
		Assert.Equal(ExpectedVersion, daemon.Version);
	}

	[Fact]
	public async Task WhenTheDaemonRefusesToShutDown_ThenTheHandshakeFailsNamingBothVersions()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = OtherVersion,
			StartedVersion = ExpectedVersion,
			ShutdownResult = DaemonShutdownRequest.Unreachable,
		};

		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None));

		Assert.Contains(OtherVersion, exception.Message);
		Assert.Contains(ExpectedVersion, exception.Message);
		Assert.Equal(0, daemon.StartCount);
	}

	[Fact]
	public async Task WhenTheDaemonDoesNotSupportShutdown_ThenTheHandshakeFailsNamingBothVersions()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = OtherVersion,
			StartedVersion = ExpectedVersion,
			ShutdownResult = DaemonShutdownRequest.Unsupported,
		};

		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None));

		Assert.Contains(OtherVersion, exception.Message);
		Assert.Contains(ExpectedVersion, exception.Message);
		Assert.Equal(0, daemon.StartCount);
	}

	[Fact]
	public async Task WhenNoDaemonIsListening_ThenTheCurrentVersionIsStarted()
	{
		var daemon = new FakeDaemonControl { Listening = false, StartedVersion = ExpectedVersion };

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(1, daemon.StartCount);
		Assert.Equal(0, daemon.ShutdownRequestCount);
		Assert.Equal(ExpectedVersion, daemon.Version);
	}

	[Fact]
	public async Task WhenTheDaemonIsMidStartup_ThenItsMissingVersionIsNotTreatedAsAMismatch()
	{
		// The port is held but the version endpoint answers only after a few polls, as a daemon that
		// has bound the socket but not finished starting does.
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = ExpectedVersion,
			StartedVersion = ExpectedVersion,
			VersionCallsBeforeAnswer = 3,
		};

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(0, daemon.StartCount);
		Assert.Equal(0, daemon.ShutdownRequestCount);
	}

	[Fact]
	public async Task WhenTheListenerNeverAnswersTheVersionEndpoint_ThenTheHandshakeFailsAsUnknownVersion()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = null,
			StartedVersion = ExpectedVersion,
		};

		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => DaemonHandshake.EnsureRunningAsync(
				new DaemonHandshakeOptions
				{
					Port = 6502,
					ExpectedVersion = ExpectedVersion,
					StartupTimeout = TimeSpan.FromMilliseconds(250),
					PollInterval = TimeSpan.FromMilliseconds(5),
				},
				daemon,
				CancellationToken.None));

		Assert.Contains("unknown", exception.Message);
		Assert.Contains(ExpectedVersion, exception.Message);
		Assert.Equal(0, daemon.StartCount);
	}

	[Fact]
	public async Task WhenAnotherBridgeAlreadyReplacedTheDaemon_ThenThisBridgeStartsNothing()
	{
		// The mismatched daemon refuses shutdown because, by the time the request arrives, a concurrent
		// bridge has already replaced it with a daemon of the expected version.
		var daemon = new FakeDaemonControl
		{
			Listening = true,
			Version = OtherVersion,
			StartedVersion = ExpectedVersion,
			ShutdownResult = DaemonShutdownRequest.AlreadyMatching,
			ReplacementVersion = ExpectedVersion,
		};

		await DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None);

		Assert.Equal(0, daemon.StartCount);
	}

	[Fact]
	public async Task WhenTheStartedDaemonReportsAnotherVersion_ThenTheHandshakeFailsNamingBothVersions()
	{
		var daemon = new FakeDaemonControl
		{
			Listening = false,
			StartedVersion = OtherVersion,
		};

		InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
			() => DaemonHandshake.EnsureRunningAsync(Options(), daemon, CancellationToken.None));

		Assert.Contains(OtherVersion, exception.Message);
		Assert.Contains(ExpectedVersion, exception.Message);
	}

	private static DaemonHandshakeOptions Options(bool forceRestart = false) => new()
	{
		Port = 6502,
		ExpectedVersion = ExpectedVersion,
		StartupTimeout = TimeSpan.FromSeconds(5),
		ShutdownTimeout = TimeSpan.FromMilliseconds(250),
		RestartTimeout = TimeSpan.FromMilliseconds(250),
		PollInterval = TimeSpan.FromMilliseconds(5),
		ForceRestart = forceRestart,
	};

	private sealed class FakeDaemonControl : IDaemonControl
	{
		private int VersionCalls;

		public string? Version { get; set; }

		public bool Listening { get; set; }

		public string? StartedVersion { get; set; }

		public string? ReplacementVersion { get; set; }

		public int VersionCallsBeforeAnswer { get; set; }

		public int StartCount { get; private set; }

		public int ShutdownRequestCount { get; private set; }

		public int RestartRequestCount { get; private set; }

		public string? LastClientVersion { get; private set; }

		public DaemonShutdownRequest ShutdownResult { get; set; } = DaemonShutdownRequest.Accepted;

		public DaemonRestartRequest RestartResult { get; set; } = DaemonRestartRequest.Unsupported;

		public bool PortFreesOnRestart { get; set; } = true;

		public bool IsListening(int port) => Listening;

		public Task<string?> TryGetVersionAsync(int port, CancellationToken cancellationToken)
		{
			VersionCalls++;
			return Task.FromResult<string?>(Version is not null && VersionCalls > VersionCallsBeforeAnswer ? Version : null);
		}

		public Task<DaemonRestartRequest> TryRequestRestartAsync(int port, CancellationToken cancellationToken)
		{
			RestartRequestCount++;

			if (RestartResult == DaemonRestartRequest.Accepted && PortFreesOnRestart)
			{
				Listening = false;
				Version = null;
			}

			return Task.FromResult(RestartResult);
		}

		public Task<DaemonShutdownRequest> TryRequestShutdownAsync(int port, string clientVersion, CancellationToken cancellationToken)
		{
			ShutdownRequestCount++;
			LastClientVersion = clientVersion;

			if (ShutdownResult == DaemonShutdownRequest.Accepted)
			{
				Listening = false;
				Version = null;
			}
			else if (ShutdownResult == DaemonShutdownRequest.AlreadyMatching)
			{
				Version = ReplacementVersion;
			}

			return Task.FromResult(ShutdownResult);
		}

		public void StartDaemon()
		{
			StartCount++;
			Listening = true;
			Version = StartedVersion;
		}
	}
}
