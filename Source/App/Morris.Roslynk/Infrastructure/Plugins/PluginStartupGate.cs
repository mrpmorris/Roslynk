namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The daemon's plugin startup, as MCP request handling sees it: plugin loading starts once the
/// host has started (so the port answers and the handshake's version probe succeeds during the
/// startup window), and the gate opens when every candidate's load has run. MCP requests wait on
/// the gate, so none is accepted before startup completes — the spec's "accepts MCP requests once
/// startup completes" — while the daemon endpoints (<c>/version</c>, <c>/shutdown</c>,
/// <c>/restart</c>, <c>/plugins</c>) stay ungated and answer during the window.
/// </summary>
public sealed class PluginStartupGate
{
	private readonly TaskCompletionSource Opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <summary>Waits until plugin startup has completed. Cancelled with the caller's token — a
	/// request abandoned during startup does not linger.</summary>
	public Task WaitAsync(CancellationToken cancellationToken) =>
		Opened.Task.WaitAsync(cancellationToken);

	/// <summary>Opens the gate: pending and later MCP requests are served. Idempotent; called when
	/// plugin startup completes, fails or is cancelled, so nothing waits forever.</summary>
	public void Open() => Opened.TrySetResult();
}
