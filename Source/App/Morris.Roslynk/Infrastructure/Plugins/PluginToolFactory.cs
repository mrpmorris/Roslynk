using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Tools;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Discovers a plugin instance's tools and creates them bound to that same initialized instance.
/// Discovery covers public instance methods, including public methods inherited from base types;
/// non-public methods and static methods are never discovered. Every exposed name is the plugin's
/// normalized name, two underscores, and the declared tool name; there are no rules on declared
/// names. When two methods produce the same exposed name — ordinary C# overloads share a method name
/// — the first in discovery order wins, the second is recorded as an error naming both methods and
/// is ignored.
/// </summary>
internal sealed class PluginToolFactory
{
	private readonly ResponseBudget Budget;
	private readonly PluginToolTracker Tracker;
	private readonly ILoggerFactory LoggerFactory;

	public PluginToolFactory(ResponseBudget budget, PluginToolTracker tracker, ILoggerFactory loggerFactory)
	{
		Budget = budget;
		Tracker = tracker;
		LoggerFactory = loggerFactory;
	}

	/// <summary>
	/// Creates the wrapped tools for one plugin. A registration failure (the SDK refusing a method
	/// it cannot bind) throws; the caller cleans up any partial registration and discards the plugin.
	/// </summary>
	public IReadOnlyList<McpServerTool> CreateTools(string pluginName, IPlugin plugin)
	{
		string normalizedPluginName = PluginNameRules.Normalize(pluginName);
		Type concreteType = plugin.GetType();
		IEnumerable<MethodInfo> candidates = concreteType
			.GetMethods(BindingFlags.Public | BindingFlags.Instance)
			.Where(method => method.DeclaringType != typeof(object) && HasToolAttribute(method));

		var created = new List<McpServerTool>();
		var seenNames = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
		foreach (MethodInfo method in candidates)
		{
			McpServerToolAttribute attribute = method.GetCustomAttribute<McpServerToolAttribute>(inherit: true)!;
			string declaredName = attribute.Name ?? method.Name;
			string exposedName = $"{normalizedPluginName}__{declaredName}";

			if (seenNames.TryGetValue(exposedName, out MethodInfo? first))
			{
				LoggerFactory.CreateLogger("Roslynk.Plugins").LogError(
					"Roslynk plugin declares two tools with the exposed name {ExposedName}: '{First}' and '{Second}' both resolve the declared tool name {DeclaredName}; the first in discovery order wins, so the second is ignored",
					exposedName,
					first.DeclaringType!.FullName + "." + first.Name,
					method.DeclaringType!.FullName + "." + method.Name,
					declaredName);
				continue;
			}

			seenNames[exposedName] = method;

			McpServerTool tool = McpServerTool.Create(
				method,
				plugin,
				new McpServerToolCreateOptions { Name = exposedName, Services = null });

			created.Add(new RoslynkTool(new PluginTool(tool, Tracker, LoggerFactory), Budget));
		}

		return created;
	}

	private static bool HasToolAttribute(MethodInfo method) =>
		method.GetCustomAttribute<McpServerToolAttribute>(inherit: true) is not null;
}

/// <summary>
/// Tracks the plugin tools' in-flight calls and owns the host-shutdown token every plugin tool
/// call's request token is linked with, so either an aborted request or a shutting-down host cancels
/// the in-flight call.
/// </summary>
internal sealed class PluginToolTracker
{
	private int InFlight;
	private TaskCompletionSource? IdleSignal;

	public PluginShutdownState Shutdown { get; } = new();

	public bool IsShuttingDown => Shutdown.IsShuttingDown;

	/// <summary>The token in-flight plugin tool calls are linked against (request abort or host shutdown).</summary>
	public CancellationToken ShutdownToken => Shutdown.ShutdownTokenSource.Token;

	/// <summary>Begins tracking one in-flight call.</summary>
	public void Enter()
	{
		Interlocked.Increment(ref InFlight);
	}

	/// <summary>Ends one in-flight call; a shutdown waiting for the call to finish is released.</summary>
	public void Exit()
	{
		if (Interlocked.Decrement(ref InFlight) == 0)
			Volatile.Read(ref IdleSignal)?.TrySetResult();
	}

	/// <summary>
	/// Signals shutdown cancellation on every in-flight plugin tool call's token and waits for those
	/// calls to finish. A tool that ignores cancellation stalls shutdown until the process is killed
	/// — the mirror of the accepted delivery stall; accepted.
	/// </summary>
	public async Task BeginShutdownAndWaitAsync()
	{
		Shutdown.BeginShutdown();
		if (Volatile.Read(ref InFlight) == 0)
			return;

		var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Volatile.Write(ref IdleSignal, signal);
		if (Volatile.Read(ref InFlight) == 0)
		{
			signal.TrySetResult();
			return;
		}

		await signal.Task;
	}
}

/// <summary>
/// The wrapper every plugin tool runs under: the token the host passes is the request's
/// cancellation token linked with the host-shutdown token, and the call is tracked so shutdown can
/// wait for it to finish. Once shutdown has begun, new calls are refused — the host stopped accepting
/// plugin tool calls. Cancellation is not mapped to a retryable failure shape: the wrapper rethrows
/// OperationCanceledException exactly as the built-in tools' wrapper does, and an informational
/// OpenTelemetry record accompanies each cancelled call naming the token's source.
/// </summary>
internal sealed class PluginTool : DelegatingMcpServerTool
{
	private readonly McpServerTool Inner;
	private readonly PluginToolTracker Tracker;
	private readonly ILogger Logger;

	public PluginTool(McpServerTool innerTool, PluginToolTracker tracker, ILoggerFactory loggerFactory)
		: base(innerTool)
	{
		Inner = innerTool;
		Tracker = tracker;
		Logger = loggerFactory.CreateLogger("Roslynk.Plugins");
	}

	public override async ValueTask<CallToolResult> InvokeAsync(
		RequestContext<CallToolRequestParams> request,
		CancellationToken cancellationToken = default)
	{
		if (Tracker.IsShuttingDown)
		{
			return RoslynkTool.ToErrorResult(
				new InvalidOperationException($"The plugin tool '{Inner.ProtocolTool.Name}' was refused: Roslynk is shutting down."));
		}

		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Tracker.ShutdownToken);
		Tracker.Enter();
		try
		{
			return await base.InvokeAsync(request, linked.Token);
		}
		catch (OperationCanceledException)
		{
			// Informational: the call did not fail; it was cancelled by an aborted request or by the
			// shutting-down host. The invocation wrapper rethrows OCE exactly as built-in tools do.
			Logger.LogInformation(
				"Roslynk plugin tool call cancelled: tool={Tool} tokenSource={TokenSource}",
				Inner.ProtocolTool.Name,
				Tracker.ShutdownToken.IsCancellationRequested ? "host shutdown" : "request abort");
			throw;
		}
		finally
		{
			Tracker.Exit();
		}
	}
}
