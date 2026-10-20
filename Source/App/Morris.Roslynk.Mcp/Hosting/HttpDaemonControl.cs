using System.Net;
using System.Net.Sockets;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Talks to whatever owns the daemon port: a TCP probe for "is anything listening", the version
/// endpoint, and the shutdown request. Every call is bounded so a half-started or wedged listener
/// cannot stall the bridge indefinitely.
/// </summary>
public sealed class HttpDaemonControl : IDaemonControl
{
	private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

	private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

	private readonly Action StartDetached;

	public HttpDaemonControl(Action startDetached)
	{
		StartDetached = startDetached ?? throw new ArgumentNullException(nameof(startDetached));
	}

	public bool IsListening(int port)
	{
		using var probe = new TcpClient();
		try
		{
			probe.Connect(IPAddress.Loopback, port);
			return true;
		}
		catch (SocketException)
		{
			return false;
		}
	}

	public async Task<string?> TryGetVersionAsync(int port, CancellationToken cancellationToken)
	{
		using var expiry = new CancellationTokenSource(RequestTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);

		try
		{
			using HttpResponseMessage response = await Client
				.GetAsync(new Uri($"http://127.0.0.1:{port}{DaemonEndpoints.VersionPath}"), linked.Token)
				.ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
				return null;

			string version = (await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false)).Trim();
			return version.Length == 0 ? null : version;
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
		{
			// Cancellation of the caller's own token must still propagate.
			cancellationToken.ThrowIfCancellationRequested();
			return null;
		}
	}

	public async Task<DaemonShutdownRequest> TryRequestShutdownAsync(
		int port,
		string clientVersion,
		CancellationToken cancellationToken)
	{
		using var expiry = new CancellationTokenSource(RequestTimeout);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(expiry.Token, cancellationToken);

		try
		{
			Uri uri = new($"http://127.0.0.1:{port}{DaemonEndpoints.ShutdownPath}" +
				$"?{DaemonEndpoints.ClientVersionParameter}={Uri.EscapeDataString(clientVersion)}");

			using HttpResponseMessage response = await Client.PostAsync(uri, content: null, linked.Token).ConfigureAwait(false);

			return response.StatusCode switch
			{
				HttpStatusCode.Accepted => DaemonShutdownRequest.Accepted,
				HttpStatusCode.Conflict => DaemonShutdownRequest.AlreadyMatching,
				HttpStatusCode.NotFound => DaemonShutdownRequest.Unsupported,
				_ => DaemonShutdownRequest.Unreachable,
			};
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return DaemonShutdownRequest.Unreachable;
		}
	}

	public void StartDaemon() => StartDetached();
}
