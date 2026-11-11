# Roslynk Plugin Specification

## Purpose

Allow independently developed plugins to extend Roslynk while sharing its existing in-memory Roslyn solution snapshots.

Plugins run inside the Roslynk process. Built-in and plugin tools are exposed through the same MCP connection.

## Goals and Non-Goals

Goals:

- One deployment per plugin at a stable path, with no version directories to manage.
- The earliest possible clean failure for an unsupported or malformed plugin — at `plugin add` time where possible, otherwise at daemon discovery — with every failure named, recorded and actionable.
- A plugin-facing contract that exposes nothing a plugin can use to alter Roslynk state: interfaces only, with host-internal implementations free to carry internal-only members.
- Plugins sharing the host's Roslyn libraries, solution snapshots and diagnostics machinery instead of duplicating them.
- Bounded daemon behavior: startup readiness, event delivery and solution calls all terminate.

Non-Goals:

- Side-by-side contract versions of one plugin; rollback means re-adding the old build.
- Classes in the contract package, or plugins implementing anything but IRoslynkPlugin.
- Plugin tools in multi_query (the one remaining design detail).
- Plugins calling Roslynk tools or writing Roslynk state.
- Hot reload, collectible load contexts, or unloading a failed plugin's assemblies.
- A Testing companion package, a rollback verb, or backup cushions.
- Deploying plugins without stopping the daemon (deferred — see Remaining design details).

## Alternatives Considered

- **api-v{version} directory layers with side-by-side deployments and name reservation** (the first published design). Rejected: reading the API version from plugin metadata makes directory-based versioning redundant, and one folder per name makes reservation meaningless. Rolling back is re-adding the old build.
- **A MetadataReader-only probe, without MetadataLoadContext.** Rejected by the author: the reflection-only context reads the ApiVersion attribute typed and disposes trivially. Retained inside the probe as the fallback decode when the attribute blob cannot resolve.
- **Per-version AssemblyLoadContexts for contract assemblies.** Superseded by per-major contract naming: distinctly named contract assemblies coexist in Roslynk's binaries folder, so no additional load contexts are ever created (see Assembly loading).
- **Collectible ALCs for the real plugin load.** Rejected: a plugin's lifetime is the host's lifetime, there is no unloading-on-error, and process exit reclaims everything — collectibility buys nothing.
- **Coalescing or dropping queued solution events for a lagging plugin.** Rejected by the author: events are never dropped; a plugin that stops consuming retains the snapshots its pending events reference.
- **Per-subscription delivery FIFOs.** Rejected as unnecessary machinery: a single delivery queue and pump give the same ordering, non-blocking and never-concurrent guarantees without per-plugin isolation — the accepted trade is that a blocking handler delays every plugin.
- **Polling instead of pushed solution events.** Rejected: latency, per-plugin timers, and it abandons the settled IObserver contract.
- **Deploying plugins without stopping the daemon, via shadow-copied plugin DLLs.** Deferred — recorded under Remaining design details.
- **A Morris.Roslynk.Plugins.Testing companion package with factory doubles.** Rejected: the contract has no operations to double; testability lives in host-injected seams (IFileSystem, IAssemblyLoader) that Roslynk's own test projects mock.
- **A `roslynk plugin rollback` verb with `.roslynk.bak` cushions.** Rejected by the author: rollback is re-adding the old build.

## PluginAPI package

A separate NuGet package provides the plugin-facing contract. It contains the contract types — interfaces, enums, attributes — plus the build targets that stamp API-version assembly metadata and a convention analyzer. It ships no host or runtime implementation types:

- IRoslynkPlugin.
- IPluginContext.
- ISolutionContext.
- ISolutionEvent.
- IUseSolutionResult&lt;T&gt;.
- ApiVersion (V1, V2, …).
- SolutionEventKind.
- UseSolutionResultState.
- RoslynkPluginFactoryAttribute.
- RoslynkApiVersionAttribute — an assembly-level attribute carrying an `ApiVersion` enum value, stamped by the package's build targets.
- The build targets that generate the API-version assembly metadata.
- A Roslyn analyzer validating plugin factory declarations, plugin tool signatures and plugin assembly naming.

Roslynk-internal classes implement the interfaces and may expose additional internal-only members that plugins never see. The interfaces are the plugin's entire view of the host.

The first contract major is the package `Morris.Roslynk.Plugins.V1`, containing the contract assembly `Morris.Roslynk.Plugins.V1.dll`. Each later breaking major ships as its own package and assembly, numbered the same way — `Morris.Roslynk.Plugins.V2` and `Morris.Roslynk.Plugins.V2.dll`, and so on. Roslynk's binaries folder carries one contract assembly per supported major, so every version's contract types coexist and the loader can serve each plugin the copy matching its probed API version (see Assembly loading). A contract package's namespace matches its assembly name (`Morris.Roslynk.Plugins.V1`), so the host can compile against every supported major without ambiguous references.

## Plugin location

The plugin directory is fixed at:

```
~/.roslynk/plugins/
```

Each plugin is deployed in a directory whose name identifies the plugin:

```
~/.roslynk/plugins/{PluginName}/
```

Example:

```
~/.roslynk/plugins/
  CallGraph/
    CallGraph.Roslynk.Plugin.dll
    ...
  CodeMetrics/
    CodeMetrics.Roslynk.Plugin.dll
    ...
```

There is no version directory layer. A plugin has exactly one deployment: deploying a different contract version replaces the folder's contents in place, and rolling back means re-adding the old build.

Deployment happens through the `roslynk plugin add` command (see CLI); manual deployment of the entry assembly and its dependencies to this location also works.

The daemon must restart to discover deployment changes.

## Plugin identity

The plugin's containing directory supplies its name.

The invariant lowercase form of that name — its normalized name — is used for identity comparison and tool prefixes.

For example, CallGraph and callgraph represent the same normalized name. On case-sensitive filesystems both directory names can exist; the collision rule is defined under Entry assembly discovery.

The name matches `[A-Za-z][A-Za-z0-9]{2,31}`: at least 3 and at most 32 characters, a letter first, then letters or digits — subject to the tool-name rules below.

The plugin name, the entry assembly's base name and the containing directory name are all `{Name}` — one rule everywhere.

## Entry assembly discovery

Discovery examines every folder directly beneath the plugins root, in ordinal order. A folder is either a valid plugin deployment or it is reported as an OpenTelemetry error and skipped. Entry-DLL-shaped files loose in the plugins root are examined the same way.

A folder is a candidate only when its name matches the plugin-name regex and it contains exactly one entry assembly matching:

```
{Name}.Roslynk.Plugin.dll
```

The plugin name, the entry assembly's base name and the containing directory name are all `{Name}`. The analyzer enforces the output assembly name at build time; the loader verifies that the directory named `{Name}` contains `{Name}.Roslynk.Plugin.dll`. Incorrect naming is reported and skipped:

- A folder whose name does not match the plugin-name regex — including the `api-v{version}/{Name}/` layout of earlier plugin releases — is reported as `folder-name-invalid`, with the corrective action.
- A folder matching the regex but containing no `{Name}.Roslynk.Plugin.dll` is reported as `entry-dll-missing`.
- A `{Name}.Roslynk.Plugin.dll` whose base name does not match its containing folder's name is reported as `name-mismatch`.
- A `*.Roslynk.Plugin.dll` loose in the plugins root (no subfolder) is recorded with its own corrective action — "move it into a folder named after its base name" — and skipped.
- A folder whose normalized name collides with an earlier folder's is reported as `duplicate-name` and skipped; the earlier folder in ordinal order wins.

For each candidate, the entry assembly is probed before any executable load:

1. A `MetadataLoadContext` is created for the entry DLL. Its resolver is fed the runtime facades plus the contract assembly resolved from the plugin's own deps folder (`AssemblyDependencyResolver`) — the copy matching the contract major the plugin references, not every supported version at once; distinct contract assembly names make that unambiguous.
2. The API-version attribute is read. If the attribute type cannot resolve, the attribute blob is decoded directly with `MetadataReader` (the enum value is its underlying integer); an unreadable blob is `malformed`.
3. The probe also reads the entry assembly's referenced-assembly versions. A plugin referencing a `Microsoft.CodeAnalysis.*` version newer than the host's loaded set is recorded as a warning (see OpenTelemetry errors) — the load proceeds, because forward binding is sometimes safe, but the first skew symptom is now data instead of a per-call `MissingMethodException`.
4. The context is disposed. The probe is reflection-only: nothing executes, nothing is pinned, and there is nothing left to unload.
5. If the attribute is missing, or its version falls outside the supported range, the folder is skipped with an OpenTelemetry error stating the probed version (or that none was found) and whether it is too high or too low to be supported.

The supported range is defined by minimum and maximum API-version constants at the top of the loader class. The CLI shares the same constants (see plugin add).

A failed probe, a failed load or a failed initialization leaves the folder in place for the user to fix or remove; the next daemon start retries it.

## Assembly loading

Each plugin loads into its own AssemblyLoadContext, with AssemblyDependencyResolver resolving its private dependencies. The context is a default (non-collectible) ALC: a plugin's lifetime is the host's lifetime, there is no unloading-on-error, and process exit reclaims everything.

When resolving the plugin's references, the loader checks the Roslynk binaries folder first and the plugin's own folder second. The Roslynk binaries folder carries one contract assembly per supported contract major (see PluginAPI package) — distinctly named, so they coexist in the default context and no additional load contexts are created — and the loader serves the plugin the copy matching its probed API version, never the contract copy sitting in the plugin's own folder. A plugin that binds to its own contract copy would see a different `IPluginContext` type from the host's, and the factory invocation would fail on parameter binding.

The loader likewise forwards `Microsoft.CodeAnalysis.*` requests to the host's loaded assemblies regardless of the requested version — ALCs have no binding redirects, so version-forwarding is implemented in the loader's resolution. Roslyn assemblies crossing the host/plugin boundary are shared, so both sides use the same Microsoft.CodeAnalysis.Solution, Project and related types. A dependency that cannot be satisfied this way fails that plugin's load and is reported as an OpenTelemetry error.

Private dependencies may differ between plugins where they do not cross the shared contract boundary.

The loader's filesystem and assembly-loading dependencies are injected interfaces — `IFileSystem` and `IAssemblyLoader` — so Roslynk's own test projects can mock them.

## Plugin contract

```
public interface IRoslynkPlugin :
    IObserver<ISolutionEvent>
{
}
```

A plugin's lifetime is exactly the host's lifetime: OnCompleted (fired only at host shutdown) is its final callback, and the process exit that follows reclaims everything it holds. There is no IDisposable on the contract.

## Factory declaration

```
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RoslynkPluginFactoryAttribute : Attribute
{
}
```

Each plugin entry assembly exposes exactly one factory method decorated with [RoslynkPluginFactory].

Requirements:

- The declaring class is public and publicly accessible.
- The method is public and static.
- The method is non-generic and its declaring type is non-generic.
- The method accepts exactly one IPluginContext parameter.
- The declared return type is nullable IRoslynkPlugin (IRoslynkPlugin?). A null return is a failure of the plugin's own initialization — not a Roslynk failure: Roslynk records it as an OpenTelemetry error and ignores it from there. The plugin does not run, and the next daemon start invokes the factory again.
- The method returns a non-null initialized instance in the successful case.

The method name is unrestricted. Discovery uses the attribute.

Example:

```
public static class PluginFactory
{
    [RoslynkPluginFactory]
    public static IRoslynkPlugin? Create(IPluginContext context)
    {
        // Configure dependencies, optionally using a private DI container.
        return new CallGraphPlugin(context);
    }
}
```

The plugin controls its construction, dependency injection and internal services.

The factory completes when the plugin is ready to receive events and tool calls. Background analysis continues independently after construction.

Factory discovery tolerates partial type materialization: `Assembly.GetTypes()` can throw `ReflectionTypeLoadException` when unrelated types in the plugin fail to materialize (an unresolvable base or interface on a type the factory does not need). Discovery scans the non-null entries of that exception's `Types` and fails only when the factory's own declaring type is among the failures. The analyzer validates build-time source, not the deployed assembly's runtime resolvability, so this tolerance belongs to the loader.

## Factory analyzer

The PluginAPI NuGet package includes a Roslyn analyzer enforcing plugin conventions. Referencing the PluginAPI package applies the analyzer automatically to the consumer project.

Analyzer errors cover:

- Missing factory.
- Multiple decorated factory methods.
- Factory declared outside a publicly accessible public class.
- Non-public or non-static factory method.
- Generic factory method or generic declaring type.
- Incorrect parameters.
- Incorrect declared return type.
- Output assembly name not matching `{Name}.Roslynk.Plugin.dll`.
- An exposed tool name over the 64-character limit, or containing characters outside the allowed set.
- A plugin tool method whose first parameter is not `string solutionId`, or which lacks a `CancellationToken` parameter.
- A plugin tool method whose declared name contains `__`.

The analyzer targets plugin entry projects identified by the entry-assembly naming convention, gated on the compilation's assembly name.

The analyzer sees the compilation, not the filesystem, so it cannot enforce the deployment folder name; the loader checks folder name = DLL base name.

The analyzer is build-time convenience, not the loader's authority: a manually deployed assembly never ran it. Roslynk therefore validates at load time whatever it needs to invoke a plugin safely — the deployment folder name and entry DLL presence, the API-version attribute and supported range, the factory's presence and shape, and each exposed tool's name.

Factory exceptions are runtime initialization failures detected at load time. A null return is the plugin-initialization error described under Factory declaration — recorded at error severity, not a Roslynk failure, and ignored thereafter.

## Factory ownership

Before the factory successfully returns, it owns cleanup if construction fails.

After successful return, Roslynk owns the returned plugin for the host's lifetime.

If subscription or tool registration fails after the factory returns, Roslynk cleans up partial registration and the plugin does not run.

The plugin performs no teardown of its own (see Plugin contract); the process exit that follows OnCompleted reclaims its resources, including any private DI container it owns.

## Plugin context

IPluginContext provides host facilities:

- An ILoggerFactory the plugin creates its category loggers from.
- UseSolutionAsync(solutionId, callback), loading the solution if needed, waiting for the build, and running the callback against the current snapshot (see Solution resolution).
- The process-wide response budget (see Result size).

Roslynk subscribes the returned plugin to solution events through its IObserver implementation and owns that subscription.

## Startup sequence

Roslynk:

1. Enumerates the folders directly beneath the plugins root in ordinal order, along with any entry-DLL-shaped files loose in the root.
2. Rejects folders with incorrect naming — `folder-name-invalid`, `entry-dll-missing`, `name-mismatch`, `duplicate-name` — with OpenTelemetry errors.
3. Probes each remaining candidate's entry assembly reflection-only (MetadataLoadContext), reads its API-version attribute and referenced-assembly versions, and validates both against the supported range, disposing the probe before the next candidate. A missing attribute, an unreadable blob, or a version outside the supported range is an OpenTelemetry error and the folder is skipped.
4. Loads supported plugins fully, one per normalized name, into their own default AssemblyLoadContexts. Each plugin's remaining load sequence — full load, factory invocation, tool preparation — runs within a per-plugin startup budget (`Roslynk:PluginStartupTimeout`, default 30 seconds).
5. Finds and validates each plugin's decorated factory.
6. Creates the appropriate IPluginContext.
7. Invokes the factory.
8. Validates the plugin's discovered tools (exposed-name limits, duplicate detection).
9. Subscribes the plugin to solution events.
10. Registers its tools on the plugin instance the factory returned.
11. Accepts MCP requests once startup completes — bounded by the per-plugin startup budgets.

A plugin that fails to load or initialize — including one that exceeds its startup budget — is recorded with the corresponding failure stage and does not run; its folder is left in place for the user to fix or remove, and the next daemon start retries it.

A factory that returns null is a failure of the plugin's own initialization, not of Roslynk: Roslynk records it as an OpenTelemetry error and ignores it from there. The plugin does not run; the next daemon start invokes the factory again.

## CLI

Roslynk's command line uses noun-verb subcommands, following the .NET command-line design guidance (a grouping command names an area; subcommands name verbs; options carry parameters, never actions).

```
roslynk                        Starts the daemon (foreground), as today.
roslynk stdio                  Runs the MCP stdio bridge, as today.
roslynk --help                 Lists all verbs.
roslynk plugin add {path}      Validates and deploys a plugin, then restarts the daemon.
roslynk plugin remove {name}   Removes a plugin, then restarts the daemon.
roslynk plugin list            Lists loaded plugins from the running daemon.
```

### plugin add

`roslynk plugin add {path}` copies the plugin's entry assembly and its dependencies into the plugin directory. `{path}` is the path to the entry assembly, or to a folder containing exactly one `*.Roslynk.Plugin.dll`; anything else — including a file whose name lacks the `.Roslynk.Plugin.dll` suffix — is an error and nothing is deployed. The target folder name is the substring before `.Roslynk.Plugin.dll` in the entry assembly's file name.

Validation happens before the daemon is touched. The command probes the source DLL exactly as discovery does — a reflection-only read of the API-version attribute. A malformed or unsupported version is reported as an error, nothing is deployed, and the daemon keeps running. The error names the probed version and the supported range.

On success the command stops the running daemon (if any) — releasing the file locks the daemon's AssemblyLoadContexts hold on plugin DLLs — stages the deployment into a sibling temporary folder and swaps it into `{Name}/` by deleting the existing folder and renaming the staged one into place, then starts the daemon again from the CLI's own binary. A crash during staging leaves the previous deployment intact; a crash between the delete and the rename can leave the plugin absent — visible in `roslynk plugin list`, and recovered by running `plugin add` again. Replacing the whole folder means no stale private dependencies survive from the previous version.

Stopping the daemon disconnects every MCP client: in-flight calls fail and all loaded solutions are evicted. A stdio bridge that loses its daemon re-runs the standard handshake — probing the port and starting the daemon if needed — before its next request, so every client reconnects automatically; calls in flight during the gap return the standard retryable failure shape. A test must prove the bridge recovers across a daemon replacement.

The CLI and the daemon share one source for the supported-version range.

### plugin remove

`roslynk plugin remove {name}` deletes the named plugin's directory. The command stops the running daemon (if any), removes the directory, then starts the daemon again from the CLI's own binary. The restart's client impact is the same as plugin add's.

### plugin list

`roslynk plugin list` ensures the daemon is running (starting it via the standard handshake if needed), then asks it for its plugin registry over a new loopback endpoint (`GET /plugins`) which returns each plugin's name, API contract version and, for failed plugins, the failure stage and reason — the same facts the OpenTelemetry error records carry, exposed as queryable state. The registry also reports the daemon's supported API-version range, so a CLI and a daemon of different versions cannot disagree silently.

Output lists loaded plugins, then failed ones:

```
CallGraph (contract v2)
CodeMetrics (contract v1)

Failed:
SomeName (contract v2): (Failure reason)
```

When no plugin has failed, the Failed section is omitted.

## OpenTelemetry errors

Failed plugin loads and skipped folders are emitted as error-severity log records through Roslynk's OpenTelemetry logging pipeline.

Failure records include:

- Plugin name, when known.
- API contract version, when the probe produced one (a malformed or attribute-less DLL has none).
- Plugin folder.
- Entry assembly path, when identified.
- Failure stage — `folder-name-invalid`, `entry-dll-missing`, `name-mismatch`, `duplicate-name`, `unsupported-version`, `missing-attribute`, `malformed`, `unreadable-or-locked`, `dependency-unresolved`, `tool-name-invalid`, `startup-timeout`, `factory-failure`, `subscription-failure`.
- Error reason.
- Exception details, when available.

Every stage names the corrective action where one exists ("move the DLL into a folder named 'CallGraph'"). A transient condition — a DLL locked by antivirus, a partially copied folder — is recorded as `unreadable-or-locked`, distinguishable from `malformed`, and is retried on the next daemon start.

Duplicate-name records include both folder paths and state that ordinal order selected the winner.

Version-out-of-range records include the probed entry assembly's version and whether it is too high or too low to be supported.

A factory that returns null is recorded at error severity as a plugin-initialization failure — not a Roslynk failure — and carries no failure stage; Roslynk ignores it thereafter.

A probe-detected Roslyn version skew is a warning-severity record naming the plugin, the referenced `Microsoft.CodeAnalysis` version and the host's loaded version.

Error reporting identifies the plugin's underlying exception rather than only a reflection invocation wrapper.

Records also flow to the ordinary daemon log, so a user who never opens an OpenTelemetry dashboard still has a durable surface; the console option below and `roslynk plugin list` are the interactive triage paths.

An option exists to surface OpenTelemetry output on the console (see issue [#201](https://github.com/mrpmorris/Roslynk/issues/201)): `Errors`, `Warnings` or `All`, for daemons launched manually in the foreground; the stdio bridge never enables it, keeping protocol stdout clean.

## Solution events

Roslynk publishes an IObservable and subscribes each initialized plugin as an observer.

Plugins decide which events to process.

```
public enum SolutionEventKind
{
    Building,
    Ready,
    Failed,
    Evicted
}

public interface ISolutionEvent
{
    SolutionEventKind Kind { get; }

    string SolutionId { get; }

    Solution? Solution { get; }

    string? ErrorMessage { get; }
}
```

The host implements the interface with an internal sealed class carrying init-only required properties; the interface is the plugin-facing shape, and the host's implementation may add internal members.

Solution means Microsoft.CodeAnalysis.Solution.

`SolutionId` is the absolute solution path with forward slashes — the same handle built-in tools echo, and the value a plugin passes to IPluginContext.UseSolutionAsync.

### Event meanings

| Kind | Meaning | Solution |
|---|---|---|
| Building | An initial load or rebuild started, or a solution was opened before its first load finished | null until the first load completes; the previous snapshot during a rebuild |
| Ready | A load, rebuild or incremental update published a usable snapshot | The published snapshot |
| Failed | An initial load or rebuild failed | null |
| Evicted | The solution was unloaded (idle eviction) | null |

Failed includes an explanatory ErrorMessage. A Failed solution is not gone: the host retries the rebuild (a watcher fold or reload_solution), and the plugin's next Ready signals recovery — so Failed is no trigger for a plugin to release caches. Eviction is its own Evicted kind (Solution is null); releasing caches for a SolutionId is Evicted's job, where retrying is meaningless.

The transient Updating status an incremental write publishes while it applies produces no event; the write's completion is one Ready event. A failed write produces no event: the previous snapshot is republished and only that operation fails.

There is no recovered marker: a Failed solution retried successfully (for example via reload_solution) delivers an ordinary Ready event, and ErrorMessage rides only on the Failed event that carried it.

### Snapshot semantics

Solutions and their projects are immutable Roslyn snapshots.

A plugin can retain a snapshot for analysis while Roslynk publishes later snapshots. Results calculated from an older snapshot describe that older snapshot.

A plugin running a long task should compare the snapshot it is working from against the latest event's Solution for that SolutionId, and abandon work against a superseded snapshot. The host does not interrupt in-flight plugin work.

Subscription is passive with respect to solution loading, compilation and diagnostics.

Plugins release snapshots and derived state when they no longer need them, and when an Evicted event ends their interest in a SolutionId.

### Event delivery

A newly subscribed plugin receives an event for the current state of every solution already known: Ready (with the snapshot) or Building (with a null Solution) for open solutions, and Failed for solutions that failed to load before it subscribed. These initial notifications let a plugin reconstruct current state without gaps.

Publishing an event never waits for a plugin. When a publication raises an event, the host enqueues it and returns immediately; the write channel, the write lock and the publication thread — on the initial-load, write, rebuild and fault paths alike — are never occupied by a plugin's handler. A single delivery pump dequeues events in publication order and invokes OnNext on every subscription in turn, one event at a time. Consequences:

- Events preserve their publication order, globally and per solution.
- A plugin's OnNext is never invoked concurrently with itself.
- The write pipeline never waits for a plugin handler.
- Host calls are safe from a handler: by the time an event is delivered, the publication that raised it has completed and released its locks.
- Delivery is shared: a handler that blocks delays every plugin's delivery, and one that blocks forever stalls it until process exit. OnNext must return promptly; long work belongs on the plugin's own threads, and the host does not interrupt handlers.

Initial state is delivered through the same queue as live events: the subscription is installed first, then the initial-state events are enqueued, so a live event published during replay queues behind it in publication order. A plugin may observe a duplicate Ready; nothing is lost and no second delivery path exists.

Events are never dropped. Pending events are delivered in order; if the process exits first, they are lost with it.

An observer that throws is the plugin's own bug: the host catches the exception, records it as an OpenTelemetry error, and the pump continues with the next subscription. OnError is never called — the IObserver contract's error channel is unused; OnCompleted remains the subscription's only terminal callback.

### Event completion

OnCompleted is enqueued when the host is shutting down, after all pending events; it is not called per solution, and it is each plugin's final callback: the host tears down everything the plugin holds after it, by process exit.

A plugin's subscription therefore spans the host's lifetime; the plugin learns that a specific solution is gone from that solution's Evicted event.

## Solution resolution

IPluginContext.UseSolutionAsync loads a solution if it is not already loaded, waits for the build, then runs the plugin's callback against the current snapshot. The solutionId is the same handle built-in tools accept: the solutionId echoed by open_solution, or an absolute path in either slash form.

```
public ValueTask<IUseSolutionResult<T>> UseSolutionAsync<T>(
    string solutionId,
    Func<ISolutionContext, CancellationToken, ValueTask<T>> use,
    CancellationToken cancellationToken = default);
```

The host resolves the instance through the same registry path built-in tools use, waits up to the server-side load timeout (`Roslynk:LoadWaitSeconds`, default 30 seconds) for an in-flight initial load, runs the callback, and then verifies that the snapshot the callback ran against is still current (a reference comparison against the instance's published model). A still-building solution after the load wait, and a superseded snapshot, cause an internal retry. The retry loop observes the caller's cancellation token and the same load-timeout deadline; a call whose snapshot keeps being superseded until the deadline expires returns Faulted with a fault message naming repeated supersession — a callback may therefore run more than once per call. If the solution is evicted while a callback runs, the freshness check returns Faulted (solution evicted during callback) without retry; the snapshot the callback holds stays valid.

While a solution's rebuild has failed, the published model is Faulted and `UseSolutionAsync` returns Faulted. The previous workspace stays retained until a host-side retry — a watcher fold or reload_solution — publishes a fresh Ready, and snapshots plugins already hold remain valid. Failed is not Evicted: releasing caches is Evicted's job.

```
public enum UseSolutionResultState
{
    /// <summary>The callback ran against a ready, current snapshot.</summary>
    Success,

    /// <summary>No solution file exists at the requested path. The callback did not run.</summary>
    NotFound,

    /// <summary>The load failed. The callback did not run. FaultMessage carries the build/load failure.</summary>
    Faulted
}
```

```
/// <summary>
/// The outcome of a UseSolutionAsync call. Only the host creates instances; the State and the
/// payload always agree — Success carries both Solution and Result, Faulted carries FaultMessage,
/// NotFound carries neither.
/// </summary>
public interface IUseSolutionResult<T>
{
    /// <summary>Why the call ended. The payload properties always agree with it.</summary>
    UseSolutionResultState State { get; }

    /// <summary>The load failure reason. Set only when the state is Faulted.</summary>
    string? FaultMessage { get; }

    /// <summary>True when the callback ran against a ready, current snapshot.</summary>
    bool IsSuccess { get; }

    /// <summary>
    /// The snapshot the callback ran against.
    /// Throws InvalidOperationException when the state is not Success.
    /// </summary>
    Solution Solution { get; }

    /// <summary>
    /// The callback's return value.
    /// Throws InvalidOperationException when the state is not Success.
    /// </summary>
    T Result { get; }
}
```

The host implements the interface with an internal sealed class whose private constructor and static `Success`/`Fail`/`NotFound` factories keep State and payload in agreement; the factories are host internals and are not part of the plugin-facing contract.

Reading Solution or Result on a non-Success result throws InvalidOperationException naming the state — the consumer checks IsSuccess (or State) before touching either.

## Solution last-used tracking

A solution's last-used time records when a solution was last used, and drives idle eviction: a solution unused for the configured idle window — `Roslynk:IdleEvictionMinutes`, default 30 — is evicted.

Every access to a solution resolves its instance through the registry, and the registry updates the last-used time on resolution — before waiting on a load (so eviction cannot close a solution a call is waiting on) and again after. Built-in tools, `UseSolutionAsync`, and plugin tool calls that reach their solution through the plugin context all touch through this one path; no ASP.NET middleware or invocation wrapper participates. A failed call still counts as use: resolving the instance indicates the user still wishes to use that solution.

A plugin tool that never resolves the solution it was given does not keep it alive. `UseSolutionAsync` is the means by which a plugin declares and refreshes its interest. get_solution_status does not resolve an instance and therefore never touches: listing solutions must not keep them alive.

## Compilation and diagnostics

The host creates an ISolutionContext for the snapshot passed to each `UseSolutionAsync` callback. It exposes these members:

```
public interface ISolutionContext
{
    Solution Solution { get; }

    ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default);
}
```

Plugins use `context.Solution` to access projects and request semantic compilations on demand with `Project.GetCompilationAsync`.

`GetDiagnosticsAsync` returns compiler, analyzer and Razor diagnostics of every severity, including hidden diagnostics, for `context.Solution`. Plugins filter the returned diagnostics themselves.

`ISolutionContext.GetDiagnosticsAsync` and the built-in `get_diagnostics` tool use the same diagnostics pipeline defined in [#202](https://github.com/mrpmorris/Roslynk/issues/202), which computes in two modes: compiler-only and analyzer-inclusive. For the same solution snapshot and analyzer mode, whichever caller arrives first starts the computation and caches its `Task`. The other caller awaits that exact same `Task`, whether it is still running or has already completed. The plugin always requests the analyzer-inclusive mode. The built-in tool applies its output filters after awaiting the shared task.

Plugin-initiated diagnostics are the same cached computations `get_diagnostics` uses: the per-snapshot task cache deduplicates across callers, and no separate queue or priority applies to plugin requests.

If an edit publishes a new snapshot during the callback, the context still provides diagnostics for the original snapshot. The snapshot check described in Solution resolution (a reference comparison against the instance's published model, performed after the callback returns) retries the callback with a context for the new snapshot; a callback may therefore run more than once per call.

## Plugins are read-only

Plugins do not have access to Roslynk tools that modify state. Plugins are fed interfaces: properties are read-only, and the objects they receive are immutable Roslyn models or other interfaces. No method on any plugin-facing interface can alter Roslynk state.

## Tool discovery

Plugin tool methods use the MCP SDK's [McpServerTool] attribute.

Roslynk inspects the concrete type of the plugin instance returned by the factory.

For each discovered tool method, it creates an McpServerTool bound to that same initialized instance.

The SDK supplies tool descriptions, parameter schemas, argument binding and result conversion.

Discovery covers public methods, including public methods inherited from base types; non-public methods are never discovered.

## Tool signature convention

Every plugin tool method declares, by analyzer enforcement:

1. `string solutionId` as its first parameter.
2. A `CancellationToken` parameter.

The solutionId binds normally through the MCP SDK — it is a real, published parameter, not schema fiction. The host does not rewrite the published metadata to inject or remove it.

The token the host passes is the request's ASP.NET cancellation token linked with the host-shutdown token (CancellationTokenSource.CreateLinkedTokenSource), so either an aborted request or a shutting-down host cancels the in-flight tool call. Cancellation is not mapped to a retryable failure shape: the invocation wrapper rethrows OperationCanceledException exactly as it does for built-in tools, and the MCP transport reports the cancelled call. An OpenTelemetry record accompanies each cancelled plugin tool call naming the token's source (request abort or host shutdown). Shutdown cancelling in-flight tool calls (see Shutdown) is this same shutdown token flowing through the link, not a separate mechanism.

No per-call response budget reaches a plugin tool. multi_query — the built-in batching tool that executes several read-only operations in one request under per-call budget slices — does not accept plugin tools (see Result size).

## Result size

Plugin tool results are held under the server-wide response budget (`Roslynk:MaxResponseChars`, default 80,000 characters) by the same wrapper backstop that applies to built-in tools that cannot page: a result over the budget is cut after its last whole line and marked with `outputTruncated` headers on an otherwise non-error result. The process-wide budget value is available to the plugin on IPluginContext at initialization, so a plugin can size its own results before the backstop cuts them.

Plugins are excluded from multi_query, so no per-call budget slice is passed to a plugin tool.

## Tool naming

Roslynk prefixes every plugin tool name:

```
{normalized plugin name}__{declared tool name}
```

Examples:

| Plugin folder | Declared tool name | Exposed tool name |
|---|---|---|
| Plugin1 | tool_1 | plugin1__tool_1 |
| Plugin2 | some_other_tool | plugin2__some_other_tool |

A declared tool name must not contain `__` (analyzer error): the double underscore is the MCP client's server/tool separator (`mcp__{server}__{tool}` is prepended by harnesses, never by Roslynk), and a tool named `a__b` would be indistinguishable from plugin `a`'s tool `b` in prefixed clients.

Plugin-to-built-in collisions are structurally impossible: the mandatory plugin prefix cannot collide with a built-in name. Plugin-to-plugin collisions cannot occur: each normalized plugin name is registered once. Registration still rejects a duplicate exposed name defensively, reported as an OpenTelemetry error.

The full exposed name `{pluginName}__{toolName}` must fit MCP tool-name limits (letters, digits, underscore, hyphen). The analyzer caps `{assembly base name}__{declared tool name}` at 64 characters — the assembly base name equals the plugin name in every deployment the loader accepts — which bounds the declared tool name at 30 characters for a maximum-length plugin name. Because a manually deployed assembly never ran the analyzer, the loader re-checks each discovered tool's exposed name for length and character set; a violation is recorded as `tool-name-invalid` and that tool is not registered.

## Tool listing

Built-in and plugin tools share one MCP connection and tool collection.

A tools/list request returns both sets of tools.

Plugin tools expose their prefixed names together with their descriptions and schemas.

The stdio bridge proxies tools/list live from the daemon with no client-side cache, so a daemon restart alone refreshes the tool list every client sees.

## Tool invocation

For a request targeting plugin1__tool_1:

1. The MCP SDK finds the registered tool by its full exposed name.
2. It binds supplied arguments to the method parameters.
3. It invokes the method on the plugin instance returned by the factory.
4. It awaits asynchronous completion.
5. The invocation wrapper applies the result-size backstop and returns the result through the existing MCP connection.

The wrapper performs no last-used tracking: the plugin reaches its solution through IPluginContext.UseSolutionAsync, and the registry path that resolves the instance updates the last-used time (see Solution last-used tracking).

Registration associates each full exposed tool name with its target method and plugin instance.

Request cancellation reaches the tool method's CancellationToken parameter.

## Shutdown

Roslynk:

1. Stops accepting new plugin tool calls.
2. Signals shutdown cancellation on every in-flight plugin tool call's token, and waits for those calls to finish within the daemon's existing stop timeout.
3. Unloads solutions as part of shutdown.
4. Ends event delivery: enqueues OnCompleted as the final item on the delivery queue — after all pending events, it is every plugin's final callback. Events still pending when the process exits are lost with it.
5. Exits the process, reclaiming everything the plugins hold.

Process exit reclaims AssemblyLoadContexts, retained snapshots and everything the plugins hold (see Plugin contract).

## API compatibility

The contract has two sides with different evolution rules.

IRoslynkPlugin is implemented by plugins. Any change to it — adding, removing or changing members — is a breaking change and requires a new contract major.

IPluginContext, ISolutionContext, ISolutionEvent and IUseSolutionResult&lt;T&gt; are implemented only by the host. Adding members to them is compatible with existing compiled plugins: old plugins never see the new members until they are recompiled. Removing, renaming or changing existing members breaks compatibility.

The two plugin-facing enums follow the consumer rule: members may be added within a major version, and plugins must tolerate unknown values — an exhaustive switch over a contract enum must include a default arm.

Breaking changes introduce a new contract major: a new `ApiVersion` enum value and a new NuGet package containing a distinctly named contract assembly (see PluginAPI package). Roslynk's binaries folder carries one contract assembly per supported major, and the loader serves each plugin the copy matching its probed API version (see Assembly loading).

Factory discovery and invocation use the contract version identified by the plugin's probed API-version attribute.

Package validation must detect accidental binary breaks against earlier supported releases, by comparing the new contract assembly's public surface against the previous release's. Roslyn dependency compatibility is validated separately from the PluginAPI contract version.

## Remaining design details

- Whether plugin tools join multi_query later (excluded for now; joining requires opening the closed read-only operations catalog, a read-only gate on qualifying tools, and a pinned-snapshot hand-off so batch slots share one generation).
- Whether to deploy plugins without stopping the daemon, via shadow-copied plugin DLLs (deferred; the stop is accepted for now).
