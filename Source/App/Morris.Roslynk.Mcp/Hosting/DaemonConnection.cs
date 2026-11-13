using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// The stdio bridge's one live MCP session to the daemon, recreated through the standard handshake
/// when the daemon is gone. Disposing the client on the way out sends the session DELETE, so the
/// daemon releases the session's solution ref-counts and idle eviction can reclaim the workspaces.
/// </summary>
internal sealed class DaemonConnection
{
	private readonly int Port;
	private readonly SemaphoreSlim Gate = new(1, 1);
	private McpClient? Client;

	public DaemonConnection(int port)
	{
		Port = port;
	}

	public async Task<McpClient> EnsureClientAsync(CancellationToken cancellationToken)
	{
		await Gate.WaitAsync(cancellationToken);
		try
		{
			if (Client is { } existing)
				return existing;

			// The standard handshake: probe the port, start the daemon if needed.
			await DaemonLauncher.EnsureRunningAsync(Port, cancellationToken);

			var transport = new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri($"http://localhost:{Port}"),
				TransportMode = HttpTransportMode.StreamableHttp,
				Name = "Roslynk daemon",
			});

			Client = await McpClient.CreateAsync(transport);
			return Client;
		}
		finally
		{
			Gate.Release();
		}
	}

	/// <summary>
	/// Runs one proxied request. A transport-level failure marks the connection dead — the
	/// in-flight call fails with the bridge's standard failure shape — and the next request
	/// re-runs the standard handshake before it runs.
	/// </summary>
	public async ValueTask<T> CallAsync<T>(Func<McpClient, CancellationToken, ValueTask<T>> call, CancellationToken cancellationToken)
	{
		McpClient client = await EnsureClientAsync(cancellationToken);
		try
		{
			return await call(client, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is McpException or HttpRequestException or IOException or ObjectDisposedException)
		{
			await Gate.WaitAsync(cancellationToken);
			try
			{
				if (ReferenceEquals(Client, client))
				{
					Client = null;
					try
					{
						// The session DELETE is a courtesy; a daemon that is already gone ignores it.
						await client.DisposeAsync();
					}
					catch
					{
						// The daemon may be gone; the next request reconnects regardless.
					}
				}
			}
			finally
			{
				Gate.Release();
			}

			throw;
		}
	}
}
