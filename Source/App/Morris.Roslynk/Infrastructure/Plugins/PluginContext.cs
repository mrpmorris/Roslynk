using Microsoft.CodeAnalysis;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The host's shutdown marker shared by every plugin-facing facility: once set, no new plugin tool
/// call is accepted and UseSolutionAsync returns Faulted without reloading — solutions were unloaded
/// as part of shutdown and no reload is attempted.
/// </summary>
internal sealed class PluginShutdownState
{
	private volatile bool ShuttingDown;

	/// <summary>The token every in-flight plugin tool call's request token is linked with.</summary>
	public CancellationTokenSource ShutdownTokenSource { get; } = new();

	public bool IsShuttingDown => ShuttingDown;

	/// <summary>Signals shutdown cancellation: in-flight plugin tool calls' tokens fire.</summary>
	public void BeginShutdown()
	{
		ShuttingDown = true;
		ShutdownTokenSource.Cancel();
	}
}

/// <summary>
/// The host's implementation of the plugin-facing context. The interfaces are the plugin's entire
/// view of the host; this class carries nothing a plugin can use to alter Roslynk state — the one
/// host-side effect the contract lets a plugin cause is UseSolutionAsync's load-on-demand.
/// </summary>
internal sealed class PluginContext : IPluginContext
{
	private readonly InstanceRegistry Registry;
	private readonly LoadWaitTimeout LoadWait;
	private readonly PluginShutdownState Shutdown;

	public PluginContext(
		InstanceRegistry registry,
		LoadWaitTimeout loadWait,
		ResponseBudget responseBudget,
		ILoggerFactory loggerFactory,
		PluginShutdownState shutdownState)
	{
		Registry = registry;
		LoadWait = loadWait;
		Shutdown = shutdownState;
		LoggerFactory = loggerFactory;
		MaxResponseChars = responseBudget.MaxChars;
	}

	public ILoggerFactory LoggerFactory { get; }

	public int MaxResponseChars { get; }

	/// <summary>
	/// Loads the solution if it is not already loaded, waits for the build, then runs the callback
	/// against the current snapshot. The retry loop observes the caller's cancellation token and the
	/// load-timeout deadline; a call whose snapshot keeps being superseded until the deadline expires
	/// returns Faulted naming repeated supersession — a callback may therefore run more than once
	/// per call. Every access resolves its instance through the same registry path built-in tools
	/// use, so last-used tracking comes from that one path; a failed call still counts as use.
	/// </summary>
	public async ValueTask<IUseSolutionResult<T>> UseSolutionAsync<T>(
		string solutionId,
		Func<ISolutionContext, CancellationToken, ValueTask<T>> use,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(solutionId);
		ArgumentNullException.ThrowIfNull(use);

		if (Shutdown.IsShuttingDown)
			return UseSolutionResult<T>.Fail(
				$"Roslynk is shutting down; solution '{solutionId}' was unloaded and will not be reloaded.");

		if (!File.Exists(solutionId))
			return UseSolutionResult<T>.NotFound();

		Stopwatch deadline = Stopwatch.StartNew();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Resolves the instance through the same registry path built-in tools use: last-used is
			// touched before the load wait (eviction cannot close a solution this call waits on) and
			// again after resolution.
			RoslynInstance instance = await Registry.GetOrBeginAsync(solutionId, cancellationToken);
			if (instance.IsClosed)
			{
				if (deadline.Elapsed >= LoadWait.Duration)
					return UseSolutionResult<T>.Fail($"Solution '{solutionId}' was unloaded before it became ready; the load wait ({LoadWait.Duration.TotalSeconds:0} seconds) expired.");
				continue;
			}

			SolutionModel? model = await ReadModelAsync(instance, cancellationToken);
			if (model is null)
			{
				if (deadline.Elapsed >= LoadWait.Duration)
					return UseSolutionResult<T>.Fail($"Solution '{solutionId}' was unloaded before it became ready; the load wait ({LoadWait.Duration.TotalSeconds:0} seconds) expired.");
				continue;
			}

			if (model.Status == SolutionStatus.Building && model.Solution is null)
			{
				if (deadline.Elapsed >= LoadWait.Duration)
					return UseSolutionResult<T>.Fail($"Solution '{solutionId}' is still building after the load wait ({LoadWait.Duration.TotalSeconds:0} seconds).");
				continue;
			}

			if (model.Status == SolutionStatus.Faulted)
				return UseSolutionResult<T>.Fail(model.FaultMessage ?? $"Solution '{solutionId}' failed to load.");

			// A ready snapshot, or an in-flight write's previous snapshot: both are the currently
			// published solution, and the callback's result describes exactly what it ran against.
			Solution snapshot = model.Solution!;
			T result = await use(new PluginSolutionContext(instance, snapshot), cancellationToken);
			instance.Touch();

			SolutionModel? fresh = await ReadModelAsync(instance, cancellationToken);
			if (instance.IsClosed || fresh is null)
				return UseSolutionResult<T>.Fail($"Solution '{solutionId}' was evicted while the callback ran; the snapshot the callback holds stays valid.");

			if (ReferenceEquals(fresh.Solution, snapshot))
				return UseSolutionResult<T>.Success(snapshot, result);

			if (deadline.Elapsed >= LoadWait.Duration)
				return UseSolutionResult<T>.Fail($"Solution '{solutionId}' kept changing; the callback was superseded repeatedly until the load wait ({LoadWait.Duration.TotalSeconds:0} seconds) expired.");
		}
	}

	/// <summary>
	/// A fenced model read that survives the instance being closed underneath the read: idle
	/// eviction and dirty-reload replacement can dispose the instance at any point, and a disposed
	/// instance's read lock throws ObjectDisposedException rather than answering.
	/// </summary>
	private static async ValueTask<SolutionModel?> ReadModelAsync(RoslynInstance instance, CancellationToken cancellationToken)
	{
		if (instance.IsClosed)
			return null;

		try
		{
			return await instance.ReadModelAsync(cancellationToken);
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
	}
}

/// <summary>
/// The host's implementation of the plugin-facing snapshot context: the immutable snapshot plus the
/// same shared diagnostics computation get_diagnostics uses — compiler, analyzer and Razor
/// diagnostics of every severity, computed once per snapshot.
/// </summary>
internal sealed class PluginSolutionContext(RoslynInstance instance, Solution snapshot) : ISolutionContext
{
	public Solution Solution { get; } = snapshot;

	public async ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
	{
		DiagnosticsResult result = await instance.RequestDiagnosticsAsync(Solution, cancellationToken);
		return [.. result.Diagnostics, .. result.Razor];
	}
}

/// <summary>
/// The host's implementation of the plugin-facing result. The private constructor and static
/// factories keep State and the payload in agreement: Success carries both Solution and Result,
/// Faulted carries FaultMessage, NotFound carries neither. Reading Solution or Result on a
/// non-Success result throws InvalidOperationException naming the state.
/// </summary>
internal sealed class UseSolutionResult<T> : IUseSolutionResult<T>
{
	private readonly Solution? SolutionField;
	private readonly T? ResultField;

	private UseSolutionResult(UseSolutionResultState state, Solution? solution, T? result, string? faultMessage)
	{
		State = state;
		SolutionField = solution;
		ResultField = result;
		FaultMessage = faultMessage;
	}

	public UseSolutionResultState State { get; }

	public string? FaultMessage { get; }

	public bool IsSuccess => State == UseSolutionResultState.Success;

	public Solution Solution =>
		State == UseSolutionResultState.Success
			? SolutionField!
			: throw new InvalidOperationException($"The UseSolutionAsync call ended in state {State}; Solution is available only when the state is Success.");

	public T Result =>
		State == UseSolutionResultState.Success
			? ResultField!
			: throw new InvalidOperationException($"The UseSolutionAsync call ended in state {State}; Result is available only when the state is Success.");

	public static UseSolutionResult<T> Success(Solution solution, T result) =>
		new(UseSolutionResultState.Success, solution, result, null);

	public static UseSolutionResult<T> Fail(string faultMessage) =>
		new(UseSolutionResultState.Faulted, null, default, faultMessage);

	public static UseSolutionResult<T> NotFound() =>
		new(UseSolutionResultState.NotFound, null, default, null);
}
