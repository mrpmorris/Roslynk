using System.Diagnostics;
using System.Reflection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Loads a discovered plugin candidate for execution: load, factory-attribute scan, the two guards
/// for assemblies that never ran a compiler check, the generic-shim dispatch, the instance check,
/// tool registration and event subscription. A failure is recorded as an error-severity OpenTelemetry
/// record and reported, never thrown; the folder stays in place and the next start re-examines it.
/// No failed-plugin state is retained anywhere — the on-disk folder is the only persistent state.
///
/// The per-plugin startup budget (<c>Roslynk:PluginStartupTimeout</c>, default 30 seconds) begins at
/// the ALC load and is a stopwatch, not a kill switch: it is checked between stages, so an in-flight
/// assembly load or factory call can overshoot it. When it expires between stages the remaining
/// stages are skipped, the plugin is recorded as <c>startup-timeout</c>, and any tools registered
/// before expiry are rolled back.
/// </summary>
public sealed class PluginLoader
{
	/// <summary>The open generic the factory claim is read from.</summary>
	private static readonly Type FactoryAttributeOpenGeneric = typeof(PluginFactoryAttribute<>);

	private readonly IFileSystem FileSystem;
	private readonly IAssemblyLoader AssemblyLoader;
	private readonly PluginToolFactory ToolFactory;
	private readonly IPluginToolSink ToolSink;
	private readonly PluginContext Context;
	private readonly SolutionEventPublisher SolutionEvents;
	private readonly LoadedPluginRegistry Loaded;
	private readonly InstanceRegistry InstanceRegistry;
	private readonly TimeSpan StartupBudget;
	private readonly string? RuntimeDirectory;
	private readonly ILogger<PluginLoader> Logger;

	internal PluginLoader(
		IFileSystem fileSystem,
		IAssemblyLoader assemblyLoader,
		PluginToolFactory toolFactory,
		IPluginToolSink toolSink,
		PluginContext context,
		SolutionEventPublisher solutionEvents,
		LoadedPluginRegistry loaded,
		InstanceRegistry instanceRegistry,
		TimeSpan? startupBudget = null,
		string? runtimeDirectory = null,
		ILogger<PluginLoader>? logger = null)
	{
		FileSystem = fileSystem;
		AssemblyLoader = assemblyLoader;
		ToolFactory = toolFactory;
		ToolSink = toolSink;
		Context = context;
		SolutionEvents = solutionEvents;
		Loaded = loaded;
		InstanceRegistry = instanceRegistry;
		StartupBudget = startupBudget ?? TimeSpan.FromSeconds(30);
		RuntimeDirectory = runtimeDirectory ?? AppContext.BaseDirectory;
		Logger = logger ?? NullLogger<PluginLoader>.Instance;
	}

	/// <summary>
	/// Runs the load pipeline for one candidate. Returns the loaded plugin, or null when the
	/// candidate failed — every failure carries its own error-severity record.
	/// </summary>
	public async Task<LoadedPlugin?> LoadAsync(PluginCandidate candidate, CancellationToken cancellationToken)
	{
		string pluginName = candidate.PluginName;
		Stopwatch stopwatch = Stopwatch.StartNew();
		bool success = false;
		cancellationToken.ThrowIfCancellationRequested();

		try
		{
			LoadedPlugin? loaded = await LoadWithinBudgetAsync(candidate, pluginName, stopwatch, cancellationToken);
			success = loaded is not null;
			return loaded;
		}
		catch (StartupBudgetExceededException expiry)
		{
			RecordFailure(
				pluginName,
				candidate.PluginFolderPath,
				stage: PluginFailureStage.StartupTimeout,
				reason: $"Startup budget expired after the {expiry.Phase} stage: {expiry.Elapsed.TotalSeconds:0.#}s elapsed against a {StartupBudget.TotalSeconds:0.#}s budget; the remaining stages were skipped and the folder will be retried on the next daemon start",
				entryAssemblyPath: candidate.EntryAssemblyPath,
				factoryType: expiry.FactoryType,
				referencedAssemblies: expiry.ReferencedAssemblies,
				exception: null);
			return null;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception)
		{
			// Any failure not covered by a named stage is recorded with its reason and no stage.
			RecordFailure(
				pluginName,
				candidate.PluginFolderPath,
				stage: null,
				reason: $"The plugin could not be loaded: {exception.Message}",
				entryAssemblyPath: candidate.EntryAssemblyPath,
				factoryType: null,
				referencedAssemblies: null,
				exception: exception);
			return null;
		}
		finally
		{
			stopwatch.Stop();
			RoslynkMeter.RecordPluginStartupDuration(PluginNameRules.Normalize(pluginName), stopwatch.Elapsed, success);
		}
	}

	private async Task<LoadedPlugin?> LoadWithinBudgetAsync(
		PluginCandidate candidate,
		string pluginName,
		Stopwatch stopwatch,
		CancellationToken cancellationToken)
	{
		// Stage 1: load the entry assembly into its own non-collectible ALC.
		Assembly entry;
		try
		{
			entry = AssemblyLoader.Load(pluginName, candidate.EntryAssemblyPath, RuntimeDirectory);
		}
		catch (BadImageFormatException exception)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, null,
				$"The entry assembly could not be loaded: {exception.Message}", candidate.EntryAssemblyPath, null, null, exception);
			return null;
		}
		catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, PluginFailureStage.UnreadableOrLocked,
				$"The entry assembly could not be read: {exception.Message}", candidate.EntryAssemblyPath, null, null, exception);
			return null;
		}

		string[] referencedAssemblies = ReadReferencedAssemblies(entry);
		EnsureWithinBudget(stopwatch, "assembly load", pluginName, candidate.EntryAssemblyPath);

		// Stage 2: scan the custom attributes for the open generic PluginFactoryAttribute<> and take T.
		Type? factoryType = ReadFactoryType(entry, out string? factoryTypeError, out PluginFailureStage? readFailureStage);
		EnsureWithinBudget(stopwatch, "factory attribute scan", pluginName, candidate.EntryAssemblyPath);
		if (factoryType is null)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, readFailureStage,
				factoryTypeError!, candidate.EntryAssemblyPath, null, referencedAssemblies, null);
			return null;
		}

		// Stage 3: the guard for assemblies that never ran a compiler check — the attribute names a
		// factory type that must implement IPluginFactory.
		if (!typeof(IPluginFactory).IsAssignableFrom(factoryType))
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, PluginFailureStage.FactoryTypeInvalid,
				$"The [assembly: PluginFactory] type argument '{factoryType.AssemblyQualifiedName}' does not implement {nameof(IPluginFactory)}",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, null);
			return null;
		}

		EnsureWithinBudget(stopwatch, "factory type guard", pluginName, candidate.EntryAssemblyPath);

		// Stage 4: invoke the factory through the generic dispatch shim — never a name-based lookup.
		IPlugin? instance;
		try
		{
			instance = (IPlugin?)InvokeFactory(factoryType, Context);
		}
		catch (TargetInvocationException exception) when (UnsatisfiableDependency(exception.InnerException) is string missing)
		{
			// The factory's first use of a type from a private dependency the deployment does not carry
			// surfaces here as the JIT's bind failure; the spec maps any unsatisfiable reference to
			// dependency-unresolved, with the missing assembly named.
			RecordFailure(pluginName, candidate.PluginFolderPath, PluginFailureStage.DependencyUnresolved,
				$"The plugin's factory could not run: the deployment does not carry '{missing}'",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, exception.InnerException);
			return null;
		}
		catch (TargetInvocationException exception)
		{
			// The plugin's underlying exception, not the reflection invocation wrapper.
			Exception cause = exception.InnerException ?? exception;
			RecordFailure(pluginName, candidate.PluginFolderPath, PluginFailureStage.FactoryFailure,
				$"The factory '{factoryType.AssemblyQualifiedName}' threw: {cause.Message}",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, cause);
			return null;
		}
		catch (Exception exception)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, PluginFailureStage.FactoryFailure,
				$"The factory '{factoryType.AssemblyQualifiedName}' could not be invoked: {exception.Message}",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, exception);
			return null;
		}

		EnsureWithinBudget(stopwatch, "factory invocation", pluginName, candidate.EntryAssemblyPath);

		// A null return is the plugin's own initialization failure, not a Roslynk failure.
		if (instance is null)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, null,
				$"The factory '{factoryType.AssemblyQualifiedName}' returned null; the plugin's initialization failed, so the plugin does not run and the next daemon start invokes the factory again",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, null);
			return null;
		}

		// Stage 5: the returned instance must be IPlugin. Otherwise the instance is dropped and no
		// references are kept — the loaded assemblies stay pinned until process exit, but the plugin
		// does not run.
		if (instance is not IPlugin plugin)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, null,
				$"The factory '{factoryType.AssemblyQualifiedName}' returned a '{instance.GetType()}', which does not implement {nameof(IPlugin)}; the instance is dropped and no references are kept",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, null);
			return null;
		}

		// Stage 6: discover and register the plugin's tools.
		IReadOnlyList<McpServerTool> tools;
		try
		{
			tools = ToolFactory.CreateTools(pluginName, plugin);
		}
		catch (Exception exception)
		{
			RecordFailure(pluginName, candidate.PluginFolderPath, null,
				$"The plugin's tools could not be registered: {exception.Message}; partial registration was cleaned up and the plugin is discarded",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, exception);
			return null;
		}

		foreach (McpServerTool tool in tools)
			ToolSink.Add(tool);

		EnsureWithinBudget(stopwatch, "tool registration", pluginName, candidate.EntryAssemblyPath, factoryType, referencedAssemblies, tools);

		// Stage 7: subscribe the plugin to solution events.
		try
		{
			await SolutionEvents.SubscribeAsync(plugin, PluginNameRules.Normalize(pluginName), RegistryStateCapture, cancellationToken);
		}
		catch (Exception exception)
		{
			foreach (McpServerTool tool in tools)
				ToolSink.Remove(tool);

			RecordFailure(pluginName, candidate.PluginFolderPath, PluginFailureStage.SubscriptionFailure,
				$"The plugin could not be subscribed to solution events: {exception.Message}; partial registration was cleaned up and the plugin is discarded",
				candidate.EntryAssemblyPath, factoryType, referencedAssemblies, exception);
			return null;
		}

		EnsureWithinBudget(stopwatch, "subscription", pluginName, candidate.EntryAssemblyPath, factoryType, referencedAssemblies, tools);

		var loadedPlugin = new LoadedPlugin(
			PluginNameRules.Normalize(pluginName),
			pluginName,
			candidate.EntryAssemblyPath,
			factoryType,
			tools.Select(tool => tool.ProtocolTool.Name).ToArray());
		Loaded.Add(loadedPlugin);

		Logger.LogInformation(
			"Roslynk plugin loaded: plugin={Plugin} folder={Folder} tools={ToolCount} elapsed={ElapsedSeconds:0.##}s",
			pluginName, candidate.PluginFolderPath, tools.Count, stopwatch.Elapsed.TotalSeconds);
		return loadedPlugin;
	}

	/// <summary>
	/// The initial state every new subscription replays, captured through the registry — the same
	/// path publications reach the pump through.
	/// </summary>
	private async Task<IReadOnlyList<ISolutionEvent>> RegistryStateCapture(CancellationToken cancellationToken)
	{
		var events = new List<ISolutionEvent>();
		foreach (RoslynInstance instance in InstanceRegistry.LoadedInstances())
		{
			SolutionModel model = await instance.ReadModelAsync(cancellationToken);
			if (SolutionEventPublisher.EventFor(model, instance.Key) is { } solutionEvent)
				events.Add(solutionEvent);
		}

		return events;
	}

	private static object? InvokeFactory(Type factoryType, IPluginContext context)
	{
		MethodInfo dispatch = typeof(PluginDispatcher)
			.GetMethod(nameof(PluginDispatcher.Dispatch), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
			.MakeGenericMethod(factoryType);
		return dispatch.Invoke(null, [context]);
	}

	private static Type? ReadFactoryType(Assembly entry, out string? error, out PluginFailureStage? readFailureStage)
	{
		try
		{
			IList<CustomAttributeData> attributes = entry.GetCustomAttributesData();
			List<CustomAttributeData> claims = attributes
				.Where(attribute => attribute.AttributeType is { } type
					&& type.IsGenericType
					&& type.GetGenericTypeDefinition() == FactoryAttributeOpenGeneric)
				.ToList();

			readFailureStage = PluginFailureStage.FactoryTypeInvalid;
			switch (claims.Count)
			{
				case 0:
					error = "The entry assembly carries no [assembly: PluginFactory<...>] attribute; add the factory claim naming a type that implements IPluginFactory";
					return null;
				case > 1:
					error = $"The entry assembly carries {claims.Count} [assembly: PluginFactory<...>] applications ({string.Join(", ", claims.Select(claim => claim.AttributeType.ToString()))}); exactly one factory claim is allowed";
					return null;
				default:
					error = null;
					return claims[0].AttributeType.GetGenericArguments()[0];
			}
		}
		catch (FileNotFoundException exception)
		{
			// Resolving the claim needs the plugin's types; one sits in a dependency that the
			// deployment does not carry, and the exception's message names it.
			error = $"The entry assembly's factory attribute could not be read: {exception.Message}";
			readFailureStage = PluginFailureStage.DependencyUnresolved;
			return null;
		}
		catch (Exception exception)
		{
			error = $"The entry assembly's factory attribute could not be read: {exception.Message}";
			readFailureStage = PluginFailureStage.FactoryTypeInvalid;
			return null;
		}
	}

	/// <summary>
	/// The missing assembly a failed factory invocation names, when its cause is a reference the
	/// deployment does not satisfy. Walks the exception chain: a JIT bind failure surfaces as a
	/// <see cref="FileNotFoundException"/> (or <see cref="FileLoadException"/>) whose <c>FileName</c>
	/// is the unsatisfiable assembly, possibly wrapped by initialization exceptions.
	/// </summary>
	private static string? UnsatisfiableDependency(Exception? cause)
	{
		for (Exception? current = cause; current is not null; current = current.InnerException)
		{
			string? fileName = current switch
			{
				FileNotFoundException missing => missing.FileName,
				FileLoadException unloadable => unloadable.FileName,
				_ => null,
			};

			if (fileName is { Length: > 0 })
				return fileName;
		}

		return null;
	}

	private static string[] ReadReferencedAssemblies(Assembly entry)
	{
		try
		{
			return entry.GetReferencedAssemblies().Select(name => name.FullName).ToArray();
		}
		catch (Exception exception) when (exception is FileNotFoundException or BadImageFormatException)
		{
			return [];
		}
	}

	private void EnsureWithinBudget(
		Stopwatch stopwatch,
		string phase,
		string pluginName,
		string entryAssemblyPath,
		Type? factoryType = null,
		string[]? referencedAssemblies = null,
		IReadOnlyList<McpServerTool>? registeredTools = null)
	{
		if (stopwatch.Elapsed <= StartupBudget)
			return;

		if (registeredTools is { Count: > 0 })
			foreach (McpServerTool tool in registeredTools)
				ToolSink.Remove(tool);

		throw new StartupBudgetExceededException(phase, stopwatch.Elapsed, pluginName, entryAssemblyPath, factoryType, referencedAssemblies);
	}

	private void RecordFailure(
		string pluginName,
		string pluginFolder,
		PluginFailureStage? stage,
		string reason,
		string? entryAssemblyPath,
		Type? factoryType,
		string[]? referencedAssemblies,
		Exception? exception)
	{
		string factoryTypeText = factoryType?.AssemblyQualifiedName ?? "(attribute not read)";
		Logger.LogError(
			exception,
			"Roslynk plugin load failed: {Reason} [stage={Stage}] plugin={Plugin} folder={Folder} entryAssembly={EntryAssembly} factoryType={FactoryType} referencedAssemblies={ReferencedAssemblies}",
			reason,
			stage?.ToStageToken() ?? "(none)",
			pluginName,
			pluginFolder,
			entryAssemblyPath ?? "(unknown)",
			factoryTypeText,
			referencedAssemblies is { Length: > 0 } ? string.Join("; ", referencedAssemblies) : "(none)");
	}

	/// <summary>The per-plugin startup budget expired between stages. Not a kill switch: the stage that
	/// was in flight completed; the remaining stages are skipped.</summary>
	internal sealed class StartupBudgetExceededException : Exception
	{
		public string Phase { get; }
		public TimeSpan Elapsed { get; }
		public Type? FactoryType { get; }
		public string[]? ReferencedAssemblies { get; }

		public StartupBudgetExceededException(string phase, TimeSpan elapsed, string pluginName, string entryAssemblyPath, Type? factoryType, string[]? referencedAssemblies)
			: base($"The startup budget for plugin '{pluginName}' expired after the {phase} stage ({elapsed.TotalSeconds:0.#}s).")
		{
			Phase = phase;
			Elapsed = elapsed;
			FactoryType = factoryType;
			ReferencedAssemblies = referencedAssemblies;
		}
	}
}

/// <summary>A plugin that finished the load pipeline: registered tools, subscribed to solution events.</summary>
public sealed record LoadedPlugin(
	string NormalizedName,
	string Name,
	string EntryAssemblyPath,
	Type FactoryType,
	IReadOnlyList<string> ToolNames);

/// <summary>The loaded plugins, keyed by normalized name. State for GET /plugins — never a failure
/// registry: failures are OpenTelemetry records and nothing more.</summary>
public sealed class LoadedPluginRegistry
{
	private readonly Dictionary<string, LoadedPlugin> Plugins = new(StringComparer.Ordinal);
	private readonly Lock Gate = new();

	public void Add(LoadedPlugin plugin)
	{
		lock (Gate)
			Plugins[plugin.NormalizedName] = plugin;
	}

	public IReadOnlyList<LoadedPlugin> All()
	{
		lock (Gate)
			return Plugins.Values.ToArray();
	}
}

/// <summary>Where registered plugin tools go. The production sink is the MCP server's tool collection.</summary>
public interface IPluginToolSink
{
	void Add(McpServerTool tool);

	void Remove(McpServerTool tool);
}
