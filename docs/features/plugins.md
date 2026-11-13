# Roslynk Plugin Specification

*   **Author:** Peter Morris
*   **Status:** Draft (under Goldfish review)
*   **Date:** 2026-11-12
*   **Decision record:** [docs/designs/plugins-contract-simplification.md](../designs/plugins-contract-simplification.md)
*   **Tracking issue:** [#199](https://github.com/mrpmorris/Roslynk/issues/199)

## Purpose

Allow independently developed plugins to extend Roslynk while sharing its existing in-memory Roslyn solution snapshots.

Plugins exist for authors of Roslyn-based MCP servers. Without them, every such server loads and holds its own copy of every solution it serves — N builds in memory for N servers, plus Roslynk's own. With them, one Roslynk process holds the model once and every plugin's tools run against that shared copy.

Plugins run inside the Roslynk process. Built-in and plugin tools are exposed through the same MCP connection.

## Goals and Non-Goals

Goals:

- One deployment per plugin at a stable path, with no version directories to manage.
- A plugin-facing contract enforced by the C# compiler at the point of claim: a wrong factory shape, a factory type that does not implement `IPluginFactory`, or a missing attribute argument is a compile error in the plugin author's build.
- Load-time validation limited to deployment naming/layout plus guards for assemblies that never ran a compiler check, recorded as named, actionable OpenTelemetry errors.
- A plugin-facing contract that exposes nothing a plugin can use to alter Roslynk state: interfaces only, with host-internal implementations free to carry internal-only members.
- Plugins sharing the host's Roslyn libraries, solution snapshots and diagnostics machinery instead of duplicating them.
- Bounded daemon behavior: load, startup and deployment waits are budgeted, and event publication never waits for a plugin; plugin callbacks, in-flight stages, and the shutdown drain are not interrupted.

Non-Goals:

- Any versioning concept in code — version numbers, version enums, version metadata. The contract surface does not break; evolution is additive (see API compatibility).
- Tool-name policing at any layer (see Tool naming).
- A failed-plugin registry: failures are OpenTelemetry records and nothing more; the on-disk folder is the only persistent state.
- Roslyn-skew detection: a plugin compiled against a newer `Microsoft.CodeAnalysis` fails per-call with `MissingMethodException`; accepted.
- A durable failure surface on Windows: the detached daemon writes no log file there; the OTLP-gated OpenTelemetry records are the failure surface (see OpenTelemetry errors).
- Hot reload, collectible load contexts, or unloading a failed plugin's assemblies (see Plugin contract).
- A Testing companion package, a rollback verb, or backup cushions.
- Plugins calling Roslynk tools or writing Roslynk state; plugin tools are excluded from `multi_query`.
- Deploying plugins without stopping the daemon (deferred — see Remaining design details).

## Alternatives Considered

Entries attributed to the author record decisions made during the design rounds captured in the linked decision record; every entry's rationale stands on its own.

- **api-v{version} directory layers with side-by-side deployments and name reservation** (the first published design). Rejected: one deployment per plugin name replaces it; there is no version metadata to key directories on, and rolling back is re-adding the old build.
- **Stamped version metadata — `ApiVersion` enum, `RoslynkApiVersionAttribute`, build targets — with a reflection-only probe.** Rejected by the author: the contract a plugin targets is already authored in its reference graph; stamped metadata and its probe duplicate that truth; the machinery — build targets plus a reflection-only probe — exists only to read it.
- **A `MetadataReader`-only structural peek at `plugin add` (reference table plus attribute table, no `MetadataLoadContext`).** Rejected by the author: no pre-deploy metadata reads at all — `plugin add` validates naming and layout only, and daemon-start discovery is the point of record for everything else.
- **Per-version AssemblyLoadContexts for contract assemblies.** Rejected: there is one contract assembly; no version selection exists.
- **A non-generic factory attribute with a `PluginFactoryType` property.** Rejected: a settable property compiles when left null, and a `Type` property enforces nothing — the generic form's constraint makes the interface claim and a closed-type argument compile errors.
- **A marker `IPluginFactory` with an analyzer signature rule.** Rejected by the author: compiler safety preferred — the static abstract member makes a wrong `Create` a compile error at every implementing class.
- **A named-method convention (reflect `Create` by name, no interface).** Rejected: nothing binds the convention to the compiler, and name-based lookup breaks on explicit interface implementations and same-named overloads.
- **A method-level `[RoslynkPluginFactory]` retained alongside the assembly attribute.** Rejected: two sources of truth for one claim; the static abstract member is strictly stronger.
- **Loader-side tool-name policing (charset, length, embedded `__`).** Rejected by the author: the harness's unique server prefix makes the server/tool boundary unique; strict-client charset/length limits are an accepted risk (see Tool naming).
- **Host-minted exposed names (sanitize/cap/dedupe).** Rejected: the exposed name diverging from the declared method name hurts debuggability and documentation.
- **Keeping the `IRoslynkPlugin` name.** Rejected by the author: renamed `IPlugin`; the namespace disambiguates against other ecosystems' `IPlugin` types.
- **An analyzer rule requiring unique declared tool names per entry assembly.** Rejected: C# overloads share a method name legitimately, and discovery already resolves the collision deterministically — first tool wins, the duplicate is recorded as an OpenTelemetry error naming both methods — so a fifth rule would add build friction without changing any outcome.
- **Per-major contract package/assembly names (`Morris.Roslynk.Plugins.V1`, `V2`, …) with side-by-side majors.** Rejected by the author: one contract assembly and no versioning in code; a hypothetical breaking change arrives as new type names (an `IPlugin2`-style pattern), never as a new version of the same name.
- **Collectible ALCs for the real plugin load.** Rejected: a plugin's lifetime is the host's lifetime, there is no unloading-on-error, and process exit reclaims everything — collectibility buys nothing.
- **Coalescing or dropping queued solution events for a lagging plugin.** Rejected by the author: events are never dropped; a plugin that stops consuming retains the snapshots its pending events reference.
- **Per-subscription delivery FIFOs.** Rejected as unnecessary machinery: a single delivery queue and pump give the same ordering, non-blocking and never-concurrent guarantees without per-plugin isolation — the accepted trade is that a blocking handler delays every plugin.
- **Polling instead of pushed solution events.** Rejected: latency, per-plugin timers, and it abandons the settled `IObserver` contract.
- **Deploying plugins without stopping the daemon, via shadow-copied plugin DLLs.** Deferred — recorded under Remaining design details.
- **A Morris.Roslynk.Plugins.Testing companion package with factory doubles.** Rejected: the contract has no operations to double; testability lives in test seams where they make tests easier to write, and in real-DLL tests against real plugin builds.
- **A `roslynk plugin rollback` verb with `.roslynk.bak` cushions.** Rejected by the author: rollback is re-adding the old build.
- **A failed-plugin registry with a Failed section in `plugin list`.** Rejected by the author: retaining a plugin's name and error existed to stop an older api-v1 deployment loading after an api-v2 failure under the per-major scheme; with one deployment per name it has no purpose — failures are OpenTelemetry records and nothing more.

## Contract package

The plugin-facing contract ships as the NuGet package `Morris.Roslynk.Plugins.Abstractions` — assembly `Morris.Roslynk.Plugins.Abstractions.dll`, namespace `Morris.Roslynk.Plugins.Abstractions`, targeting net10.0 only (a plugin project below net10.0 fails at package install). It contains the contract types plus a convention analyzer; it ships no host or runtime implementation types, no build targets, and no generated code:

- `IPlugin` — `IObserver<ISolutionEvent>`.
- `IPluginFactory` — the factory claim: `static abstract IPlugin? Create(IPluginContext context);`.
- `PluginFactoryAttribute<T>` — the assembly-level attribute: sealed, `[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]`, `T` a positional constructor argument, `where T : IPluginFactory`.
- `IPluginContext`.
- `ISolutionContext`.
- `ISolutionEvent`.
- `IUseSolutionResult<T>`.
- `SolutionEventKind`.
- `UseSolutionResultState`.
- A Roslyn analyzer validating the factory claim's presence, entry-assembly naming and plugin tool signatures.

Roslynk-internal classes implement the interfaces and may expose additional internal-only members that plugins never see. The interfaces are the plugin's entire view of the host.

## Plugin location

The plugin directory is fixed at:

```text

~/.roslynk/plugins/
```

Each plugin is deployed in a directory whose name identifies the plugin:

```text

~/.roslynk/plugins/{Name}/
```

Example:

```text

~/.roslynk/plugins/
  CallGraph/
    CallGraph.Roslynk.Plugin.dll
    ...
  CodeMetrics/
    CodeMetrics.Roslynk.Plugin.dll
    ...
```

There is no version directory layer. A plugin has exactly one deployment: deploying a new build replaces the deployment (the `plugin add` swap), and rolling back means re-adding the old build.

Deployment happens through the `roslynk plugin add` command (see CLI); manual deployment of the entry assembly and its dependencies to this location also works.

The daemon must restart to discover deployment changes.

## Plugin identity

The plugin's containing directory supplies its name.

The invariant lowercase form of that name — its normalized name — is used for identity comparison and tool prefixes.

For example, CallGraph and callgraph represent the same normalized name. On case-sensitive filesystems both directory names can exist; the collision rule is defined under Entry assembly discovery.

The name matches `[A-Za-z][A-Za-z0-9]{2,31}`: at least 3 and at most 32 characters, a letter first, then letters or digits.

The plugin name, the entry assembly's base name and the containing directory name are all `{Name}` — one rule everywhere.

## Entry assembly discovery

Discovery examines every folder directly beneath the plugins root, in ordinal order. A folder is either a valid plugin deployment, `.staging-` staging debris (handled below), or it is reported as an OpenTelemetry error and skipped. Entry-DLL-shaped files loose in the plugins root are examined the same way.

A folder is a candidate only when its name matches the plugin-name regex and it contains an entry assembly named:

```
{Name}.Roslynk.Plugin.dll
```

The same `{Name}` rule binds the folder, the DLL base name and the plugin name (see Plugin identity). The analyzer enforces the output assembly name at build time; the loader verifies that the directory named `{Name}` contains `{Name}.Roslynk.Plugin.dll`. Incorrect naming is reported and skipped:

- A folder whose name does not match the plugin-name regex — including the `api-v{version}/{Name}/` layout of earlier plugin design iterations — is reported as `folder-name-invalid`, with the corrective action.
- A folder matching the regex but containing no `{Name}.Roslynk.Plugin.dll` is reported as `entry-dll-missing`. The corrective action covers the misnamed-project case: if the folder contains a DLL named differently, the record names it — rename the DLL or fix the project's AssemblyName.
- A `*.Roslynk.Plugin.dll` whose base name does not match its containing folder's name is reported as `name-mismatch`.
- A `*.Roslynk.Plugin.dll` loose in the plugins root (no subfolder) is recorded stage-less with its own corrective action — "move it into a folder named after its base name" — and skipped.
- A folder whose normalized name collides with an earlier folder's is reported as `duplicate-name` and skipped; the earlier folder in ordinal order wins.

A folder named `.staging-{Name}` for a regex-valid `{Name}` is debris from a crashed `plugin add` staging step: it is not a candidate, produces no record, and is deleted at startup (see plugin add). Any other `.staging-` name is not a candidate either and is reported as `folder-name-invalid`.

There is no pre-load probe: nothing about a candidate DLL's contents is read before the loader loads it (see Assembly loading and Startup sequence). A DLL that is not a managed assembly, or that fails any load-time check, is reported at daemon start with the reason.

A failed load or a failed initialization leaves the folder in place for the user to fix or remove; the next daemon start retries it.

## Assembly loading

Each plugin loads into its own AssemblyLoadContext, with AssemblyDependencyResolver resolving its private dependencies. The context is a default (non-collectible) ALC: a plugin's lifetime is the host's lifetime, there is no unloading-on-error, and process exit reclaims everything. Resolution follows the same pattern as `ShadowCopyingAnalyzerAssemblyLoader` (no `Load` override, a `Resolving` event), in this order:

1. **The host's loaded assemblies.** Any assembly name the host has loaded resolves to the host's copy regardless of the requested version — ALCs have no binding redirects, so forwarding is implemented in the loader's resolution. This covers `Microsoft.CodeAnalysis.*` (Roslyn assemblies crossing the host/plugin boundary are shared, so both sides use the same `Solution`, `Project` and related types) and `ModelContextProtocol.*` (a plugin never binds its own SDK copy, so the `[McpServerTool]` attribute a plugin's tools carry always has the host's type identity and tool discovery sees it), and it covers `Microsoft.Extensions.*` and every other name the host has loaded.
2. **The Roslynk binaries folder.** It carries the contract assembly `Morris.Roslynk.Plugins.Abstractions.dll`, and requests for it resolve to the host's copy — never to the copy sitting in the plugin's own folder. A plugin that bound its own contract copy would see a different `IPluginContext` type from the host's, and the factory invocation would fail on parameter binding.
3. **The plugin's own folder**, via AssemblyDependencyResolver — private dependencies for assembly names the host does not carry. A reference that cannot be satisfied this way fails that plugin's load as `dependency-unresolved`, with the missing assembly named in the record.

A private copy therefore serves only assemblies the host does not carry; a plugin compiled against a newer version of an assembly the host loads binds the host's older copy — the same accepted skew risk as `Microsoft.CodeAnalysis.*` (see OpenTelemetry errors). Private dependencies may differ between plugins only for assembly names the host does not load.

The loader takes its filesystem and assembly-loading dependencies through injectable interfaces (`IFileSystem`, `IAssemblyLoader`) where that makes unit tests easier to write; tests that exercise real plugin DLLs use real files, following the repo's existing pattern. An interface that does not earn its place in testing is not included.

## Plugin contract

```csharp

public interface IPlugin :
    IObserver<ISolutionEvent>
{
}
```

A plugin's lifetime is exactly the host's lifetime: OnCompleted (fired only at host shutdown) is its final callback, and the process exit that follows reclaims everything it holds. There is no `IDisposable` on the contract.

## Factory declaration

Each plugin entry assembly declares its factory with one assembly-level attribute:

```csharp

[assembly: PluginFactory<PluginFactory>]
```

where the type argument implements `IPluginFactory`:

```csharp

public interface IPluginFactory
{
    static abstract IPlugin? Create(IPluginContext context);
}
```

Example:

```csharp

using Morris.Roslynk.Plugins.Abstractions;

[assembly: PluginFactory<PluginFactory>]

public sealed class PluginFactory : IPluginFactory
{
    public static IPlugin? Create(IPluginContext context)
    {
        // Configure dependencies, optionally using a private DI container.
        return new CallGraphPlugin(context);
    }
}
```

The compiler enforces every factory-shape requirement at the point of claim:

- The attribute's type argument implements `IPluginFactory` (generic constraint). A class that does not implement it, or implements `Create` with the wrong shape, is a compile error.
- The type argument is a closed type; an open generic cannot be written as a C# type argument.
- The attribute argument is mandatory; applying `[assembly: PluginFactory]` without one is a compile error.

The method name is fixed by the interface: `Create`. The factory class may be internal — reflection does not need visibility to invoke it. The factory may implement the interface member implicitly or explicitly; the loader dispatches through a generic shim rather than a name lookup, so both forms work, as do other `Create` overloads on the factory class (see Startup sequence).

The declared return type is nullable `IPlugin` (`IPlugin?`). A null return is a failure of the plugin's own initialization — not a Roslynk failure: Roslynk records it as an OpenTelemetry error and moves on; the plugin does not run, and the next daemon start invokes the factory again. A non-null return must be an initialized instance.

There is no type scanning: the loader never calls `Assembly.GetTypes()`, because the attribute names the factory type directly.

The plugin controls its construction, dependency injection and internal services.

Roslynk treats the factory's return as readiness: tool registration and subscription follow it. The host's solution analysis continues independently of construction.

## Factory analyzer

The contract package includes a Roslyn analyzer enforcing plugin conventions. Referencing the package applies the analyzer automatically to the consumer project.

Analyzer errors cover:

- A missing `[assembly: PluginFactory<...>]` attribute (nothing in the language forces its presence).
- More than one `PluginFactory<>` application on the entry assembly (distinct closed generic types each satisfy `AllowMultiple = false`, so this needs an explicit rule).
- Output assembly name not matching `{Name}.Roslynk.Plugin.dll`, where `{Name}` must satisfy the plugin-name regex.
- A declared tool method whose first parameter is not `string solutionId`, or which lacks a `CancellationToken` parameter.

Factory shape is compiler-enforced through the static abstract member and is deliberately not an analyzer rule: a class claiming `IPluginFactory` without an implementing member of the exact shape `(IPluginContext)` → `IPlugin?` does not compile — an implicit implementation must be public and static; an explicit implementation may have any accessibility. The compiler enforces nothing about the factory class itself: it may be internal, and it may be generic.

The analyzer applies to projects whose output assembly name ends in `.Roslynk.Plugin` — that suffix is the gate; the naming rule above then validates the `{Name}` prefix, which is how a misnamed `{Name}` is caught at build time.

The analyzer sees the compilation, not the filesystem, so it cannot enforce the deployment folder name; the loader checks folder name = DLL base name.

The analyzer is build-time convenience, not the loader's authority: a manually deployed assembly never ran it. Roslynk therefore validates whatever it needs to invoke a plugin safely: deployment naming and layout during discovery, then the factory attribute, the factory type's assignability to `IPluginFactory`, and the returned instance's assignability to `IPlugin`. Tool method signatures are not re-checked at load time: the analyzer owns that rule at the plugin author's build, and Roslynk trusts it — a manually deployed assembly whose tools violate the convention registers as-is.

Factory exceptions are runtime initialization failures detected at load time; a null return is the plugin-initialization error described under Factory declaration.

## Factory ownership

Before the factory successfully returns, it owns cleanup if construction fails.

After successful return, Roslynk owns the returned plugin for the host's lifetime.

If tool registration or subscription fails after the factory returns, Roslynk cleans up partial registration, records an OpenTelemetry error, and discards the plugin; it does not run.

The plugin performs no teardown of its own (see Plugin contract); the process exit that follows OnCompleted reclaims its resources, including any private DI container it owns.

## Plugin context

`IPluginContext` provides host facilities:

- An ILoggerFactory the plugin creates its category loggers from.
- UseSolutionAsync(solutionId, callback), loading the solution if needed, waiting for the build, and running the callback against the current snapshot (see Solution resolution).
- The process-wide response budget as `int MaxResponseChars { get; }` (see Result size).

Roslynk subscribes the returned plugin to solution events through its `IObserver` implementation and owns that subscription.

## Startup sequence

Roslynk:

1. Enumerates the folders directly beneath the plugins root in ordinal order, along with any entry-DLL-shaped files loose in the root.
2. Rejects folders with incorrect naming — `folder-name-invalid`, `entry-dll-missing`, `name-mismatch`, `duplicate-name` — with OpenTelemetry errors, and deletes `.staging-{Name}` debris from a crashed `plugin add` with no record (see Entry assembly discovery).
3. Loads each remaining candidate's entry assembly into its own default AssemblyLoadContext (see Assembly loading). Each plugin's load sequence — assembly load, factory invocation, tool preparation, subscription — runs within a per-plugin startup budget (`Roslynk:PluginStartupTimeout`, default 30 seconds).
4. Scans the assembly's custom attributes for the open generic `PluginFactoryAttribute<>` and takes its type argument `T`. No attribute, or more than one, is recorded as `factory-type-invalid` and the candidate is skipped.
5. Verifies `typeof(IPluginFactory).IsAssignableFrom(T)`. Otherwise `factory-type-invalid` and skipped.
6. Invokes the factory through the host's generic dispatch shim — `PluginDispatcher.Dispatch<T>(context)` bound with `MakeGenericMethod(T)` — never a name-based method lookup. A null return is the plugin-initialization failure (see Factory declaration); a throw is recorded as `factory-failure`.
7. Verifies the returned instance is `IPlugin`. Otherwise an OpenTelemetry error is recorded, the instance is dropped, and no references are kept — the loaded assemblies stay pinned until process exit, but the plugin does not run.
8. Discovers each plugin's tools (see Tool discovery) and registers them on the instance the factory returned.
9. Subscribes the plugin to solution events.
10. Accepts MCP requests once startup completes; startup is bounded by the per-plugin startup budgets.

Startup is serial per candidate: the daemon waits at most the sum of the per-plugin budgets plus any in-flight overshoot before accepting MCP requests. This is accepted as Roslynk startup cost. The budget is a stopwatch, not a kill switch — it is checked between stages, and an in-flight assembly load or factory call cannot be interrupted and can overshoot it, the same accepted stall as event delivery and shutdown. When the budget expires between stages, the remaining stages are skipped, the plugin is recorded as `startup-timeout`, and its folder is retried on the next daemon start; if tools were already registered before expiry, the registration is rolled back with the plugin's other partial state. Startup duration is metered as `roslynk.plugins.startup.duration`, tagged by plugin name and result.

A plugin that fails at any step is recorded with an error-severity OpenTelemetry record carrying the failure stage and corrective action where one exists, the reason, and exception details when available. Nothing about the failure is retained: the on-disk folder is the only persistent state, and the next daemon start retries it. Any failure not covered by a named stage — including a corrupt or non-managed entry DLL (`BadImageFormatException`) — is recorded with its reason and no stage.

## CLI

Roslynk's command line uses noun-verb subcommands, following the .NET command-line design guidance (a grouping command names an area; subcommands name verbs; options carry parameters, never actions).

```text

roslynk                        Starts the daemon (foreground), as today.
roslynk stdio                  Runs the MCP stdio bridge, as today.
roslynk --help                 Lists all verbs.
roslynk plugin add {path}      Validates and deploys a plugin, then restarts the daemon.
roslynk plugin remove {name}   Removes a plugin, then restarts the daemon.
roslynk plugin list            Lists loaded plugins from the running daemon.
```

### plugin add

`roslynk plugin add {path}` copies the plugin's entry assembly and its dependencies into the plugin directory. `{path}` is the path to an entry assembly or to a folder. A file path must name a DLL ending in `.Roslynk.Plugin.dll`; the target folder name is the substring before that suffix, and the whole folder containing the file is copied — the containing folder's own name is irrelevant in this form, so adding directly from build output works. A folder path must contain `{FolderName}.Roslynk.Plugin.dll` directly inside it — not in a parent, not in a deeper subfolder — and the folder's own name must equal `{FolderName}`; the whole folder is copied. Anything else — a file with the wrong name, a folder without the entry DLL directly inside it, a folder path whose name differs from `{FolderName}` — is an error and nothing is deployed.

Validation happens before the daemon is touched, and covers naming and layout only: the target folder name's format and length, that `{FolderName}.Roslynk.Plugin.dll` exists inside it, and — for a folder path — that the folder's name equals `{FolderName}`. The target's normalized name is also compared against existing deployments. A folder whose name differs only in case — `callgraph` beside `CallGraph` — would be skipped at every start as `duplicate-name`, so the command reports the collision and deploys nothing; a folder whose name matches exactly is the normal replace case and proceeds to the swap. There is no metadata probe: whether the DLL is a plugin at all is discovered at daemon start. Any validation failure is reported as an error, nothing is deployed, and the daemon keeps running.

On success the command stops the running daemon (if any) by calling `POST /shutdown?clientVersion=0.0.0.0` — a version no daemon reports, so the endpoint's same-version refusal never fires — releasing the file locks the daemon's AssemblyLoadContexts hold on plugin DLLs. A development daemon (0.0.1-local) refuses remote shutdown by design; the command detects one from `GET /version` and fails without deploying, directing the user to stop it manually. If the daemon does not stop within the handshake's shutdown wait, the command fails without deploying. A daemon installed as a Windows Service is reachable through the handshake: the command would stop the service-managed process and start a detached user daemon, leaving the service stopped until it is next started. That consequence is accepted: the command has no service-control path and manages only daemons reachable through the handshake.

It then stages the deployment into a sibling temporary folder named `.staging-{Name}` — deleting any `.staging-{Name}` left by a previous crashed run — and swaps it into `{Name}/` by deleting the existing folder and renaming the staged one into place, then starts the daemon again from the CLI's own binary. With multiple Roslynk installs, that restart can replace a daemon another client's bridge started; those bridges re-run their handshake and replace it back — churn the design accepts, and the swap's delete step can fail on DLLs locked by such a bridge-started daemon's contexts; re-running `plugin add` recovers, since its stop precedes the delete. A crash during staging leaves the previous deployment intact; a crash between the delete and the rename can leave the plugin absent — recovered by running `plugin add` again. Replacing the whole folder means no stale private dependencies survive from the previous version.

Staging happens while the daemon is stopped, so a `.staging-{Name}` folder found at startup is debris from a crashed deployment: it is deleted, and discovery reports nothing for it (see Entry assembly discovery).

Stopping the daemon disconnects every MCP client: in-flight calls fail and all loaded solutions are unloaded (no Evicted events — see Shutdown). A stdio bridge that loses its daemon re-runs the standard handshake — probing the port and starting the daemon if needed — before its next request, so every client reconnects automatically. The port answers before startup completes, so a bridged request arriving inside the new daemon's startup window waits there rather than failing. This reconnect behavior is new in this feature: today's bridge handshakes once at startup. Calls in flight during the gap fail with the bridge's standard failure shape. A test must prove the bridge recovers across a daemon replacement.

### plugin remove

`roslynk plugin remove {name}` deletes the named plugin's directory. The name is resolved against the plugins folder's directories before the daemon is touched: a name matching no directory there — including one that fails the plugin-name regex — prints `Not found`, and the command exits without stopping the daemon. This makes remove the recovery path for a broken same-normalized deployment (a folder `plugin add` refused for the collision): remove the broken folder first, then add again. On a case-sensitive filesystem holding two folders that share a normalized name, the folder exactly matching the given name is deleted. Otherwise the command stops the running daemon (if any) the same way `plugin add` does — `POST /shutdown?clientVersion=0.0.0.0`; a development daemon refuses by design and the command fails without deleting anything, as does a stop that does not complete within the handshake's shutdown wait. It removes the directory, then starts the daemon again from the CLI's own binary. The restart's client impact is the same as plugin add's.

### plugin list

`roslynk plugin list` ensures the daemon is running (starting it via the standard handshake if needed), then asks it for its loaded plugins over a new loopback endpoint (`GET /plugins`) which returns each loaded plugin's name and exposed tool names.

Output lists the loaded plugins:

```
CallGraph (3 tools)
CodeMetrics (2 tools)
```

There is no Failed section: failures are not retained server-side — their OpenTelemetry records are the triage surface. There is no contract-version column and no supported-version-range report: no version metadata exists.

## OpenTelemetry errors

Failed plugin loads, skipped folders and plugin runtime errors are emitted as error-severity log records through Roslynk's OpenTelemetry logging pipeline — load and discovery records carry the fields below; runtime records such as an observer that throws carry the plugin name and the exception.

Failure records include:

- Plugin name, when known.
- Plugin folder.
- Entry assembly path, when identified.
- The factory type `T`'s assembly-qualified name and defining assembly, when the attribute was read.
- The entry assembly's referenced assemblies, reported from `GetReferencedAssemblies()` on the loaded entry assembly — the `dependency-unresolved` diagnosis names the missing assembly from this list.
- Failure stage — `folder-name-invalid`, `entry-dll-missing`, `name-mismatch`, `duplicate-name`, `unreadable-or-locked`, `dependency-unresolved`, `startup-timeout`, `factory-failure`, `subscription-failure`, `factory-type-invalid` — or none, for failures without a dedicated stage — including a factory that returns null, an instance that is not `IPlugin`, a corrupt or non-managed entry DLL, a tool-registration failure, an observer that throws — where the reason carries the diagnosis. Stages are named only where the name aids triage; a tool-registration failure's diagnosis lives entirely in its reason text.
- Error reason.
- Exception details, when available.

Every stage names the corrective action where one exists ("move the DLL into a folder named 'CallGraph'"). A transient condition — a DLL locked by antivirus, a partially copied folder — is recorded as `unreadable-or-locked` and is retried on the next daemon start.

Duplicate-name records include both folder paths and state that ordinal order selected the winner.

There are no version fields in any record: no version metadata exists to record. There is no Roslyn-skew warning: a plugin compiled against a newer `Microsoft.CodeAnalysis` fails per-call with `MissingMethodException`, and that is accepted.

Error reporting identifies the plugin's underlying exception rather than only a reflection invocation wrapper.

On POSIX, records also flow to the ordinary daemon log — the daemon's console output is redirected to `daemon.log` — giving a durable surface to users who never open an OpenTelemetry dashboard; on Windows the detached daemon writes no log file, and the OTLP pipeline is the failure surface (accepted — see Non-Goals). `roslynk plugin list` shows what loaded; the OpenTelemetry records name why something did not. A console-surfacing option is proposed (tracked as issue [#201](https://github.com/mrpmorris/Roslynk/issues/201)) — `Errors`, `Warnings` or `All` for daemons launched manually in the foreground, never on the stdio bridge — and is not part of this feature.

## Solution events

Roslynk publishes an IObservable and subscribes each initialized plugin as an observer.

Plugins decide which events to process.

```csharp

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

`SolutionId` is the absolute solution path with forward slashes — the same handle built-in tools echo, and the value a plugin passes to `IPluginContext.UseSolutionAsync`.

### Event meanings

| Kind | Meaning | Solution |
|---|---|---|
| Building | An initial load or rebuild is in flight | null until the first load completes; the previous snapshot during a rebuild |
| Ready | A load, rebuild or incremental update published a usable snapshot | The published snapshot |
| Failed | An initial load or rebuild failed | null |
| Evicted | The solution was unloaded (idle eviction) | null |

Failed includes an explanatory ErrorMessage. A Failed solution is not gone: the host retries the rebuild (the file watcher's automatic rebuild or an explicit reload_solution call), and the plugin's next Ready signals recovery — so Failed is no trigger for a plugin to release caches. Releasing caches for a SolutionId is Evicted's job; retrying is meaningless there.

The transient Updating status (an existing host status, not a contract event kind) an incremental write publishes while it applies produces no event; the write's completion is one Ready event. A failed write produces no event: the previously published snapshot remains current, and only that operation fails.

There is no recovered marker: a Failed solution retried successfully (for example via reload_solution) delivers an ordinary Ready event; ErrorMessage is not repeated on it.

### Snapshot semantics

Solutions and their projects are immutable Roslyn snapshots.

A plugin can retain a snapshot for analysis while Roslynk publishes later snapshots. Results calculated from an older snapshot describe that older snapshot.

A plugin running a long task should compare the snapshot it is working from against the latest event's Solution for that SolutionId, and abandon work against a superseded snapshot. The host does not interrupt in-flight plugin work.

Subscription is passive with respect to solution loading, compilation and diagnostics.

Plugins release snapshots and derived state when they no longer need them, and when an Evicted event ends their interest in a SolutionId.

### Event delivery

A newly subscribed plugin receives an event for the current state of every solution already known: Ready (with the snapshot) for open solutions; Building for a solution whose initial load is in flight (null Solution) or whose rebuild is in flight (the previous snapshot); Failed for solutions in the Failed state before it subscribed — a failed rebuild, whose previously published snapshot remains retained and valid, or a failed initial load, which has published none (see Event meanings). These initial notifications let a plugin reconstruct current state without gaps.

Publishing an event never waits for a plugin. When a publication raises an event, the host enqueues it and returns immediately; the write channel, the write lock and the publication thread — on the initial-load, write, rebuild and fault paths alike — are never occupied by a plugin's handler. A single delivery pump dequeues events in publication order and invokes OnNext on every subscription in turn, one event at a time. Consequences:

- Events preserve their publication order, globally and per solution.
- A plugin's OnNext is never invoked concurrently with itself.
- The write pipeline never waits for a plugin handler.
- Host calls are safe from a handler: by the time an event is delivered, the publication that raised it has completed and released its locks.
- Delivery is shared: a handler that blocks delays every plugin's delivery, and one that blocks forever stalls it until the process is killed. OnNext must return promptly, and OnCompleted — delivered once at shutdown, while the host waits — must return promptly too; long work belongs on the plugin's own threads, and the host does not interrupt handlers.

Initial state is delivered through the same queue as live events: the subscription is installed first, then the initial-state events are enqueued. The initial state is captured and enqueued atomically with respect to publication — the capture takes the same lock publications take — so every event already enqueued is reflected in the captured state, and a live event published during replay queues behind it in publication order. A plugin may observe a duplicate Ready; nothing is lost and no second delivery path exists.

Events are never dropped. Pending events are delivered in order; only an abnormal exit — a crash or kill — loses them, since graceful shutdown waits for the queue to drain. Publication is fire-and-forget; the cost is memory: a pending event retains the snapshot it references until it is delivered, so a subscription that stops consuming retains those snapshots for the process's lifetime. The exposure is accepted.

An observer that throws is the plugin's own bug: the host catches the exception, records it as an OpenTelemetry error, and the pump continues with the next subscription. OnError is never called — the `IObserver` contract's error channel is unused; OnCompleted remains the subscription's only terminal callback.

### Event completion

OnCompleted is enqueued when the host is shutting down, after all pending events; it is not called per solution, and it is each plugin's final callback. The host waits for the queue to drain — every pending event, then OnCompleted — before exiting; the process exit that follows reclaims everything the plugin holds.

A plugin's subscription therefore spans the host's lifetime; the plugin learns that a specific solution is gone from that solution's Evicted event. Shutdown does not deliver Evicted events: the solution unloading during shutdown publishes nothing, and OnCompleted — enqueued after the unload — is the final callback.

## Solution resolution

`IPluginContext.UseSolutionAsync` loads a solution if it is not already loaded, waits for the build, then runs the plugin's callback against the current snapshot. The solutionId is the same handle built-in tools accept: the solutionId echoed by open_solution, or an absolute path in either slash form.

```csharp

public ValueTask<IUseSolutionResult<T>> UseSolutionAsync<T>(
    string solutionId,
    Func<ISolutionContext, CancellationToken, ValueTask<T>> use,
    CancellationToken cancellationToken = default);
```

The host resolves the instance through the same registry path built-in tools use, waits up to the server-side load timeout (`Roslynk:LoadWaitSeconds`, default 30 seconds) for an in-flight initial load, runs the callback, and then verifies that the snapshot the callback ran against is still current (a reference comparison against the instance's published model). A still-building solution after the load wait, and a superseded snapshot, cause an internal retry. The retry loop observes the caller's cancellation token and the same load-timeout deadline; a call whose snapshot keeps being superseded until the deadline expires returns Faulted with a fault message naming repeated supersession — a callback may therefore run more than once per call. If the solution is evicted while a callback runs, the freshness check returns Faulted (solution evicted during callback) without retry; the snapshot the callback holds stays valid.

While a solution's rebuild has failed, the solution's state is Failed (no fresh snapshot) and `UseSolutionAsync` returns Faulted. The previously published snapshot stays retained until a host-side retry — the file watcher's automatic rebuild or an explicit reload_solution call — publishes a fresh Ready, and snapshots plugins already hold remain valid. Failed is not Evicted: releasing caches is Evicted's job.

```csharp

public enum UseSolutionResultState
{
    /// <summary>The callback ran against a ready, current snapshot.</summary>
    Success,

    /// <summary>No solution file exists at the requested path. The callback did not run.</summary>
    NotFound,

    /// <summary>The call ended without a snapshot matching the solution's current state. FaultMessage carries the reason: a load
    /// failure (the callback did not run), repeated supersession until the deadline expired,
    /// eviction during the callback (the callback may have run), or the solution already unloaded
    /// by shutdown (the callback did not run; no reload is attempted).</summary>
    Faulted
}
```

```csharp

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

A solution's last-used time drives idle eviction: a solution unused for the configured idle window — `Roslynk:IdleEvictionMinutes`, default 30 — is evicted.

Every access to a solution resolves its instance through the registry, and the registry updates the last-used time on resolution — before waiting on a load (so eviction cannot close a solution a call is waiting on) and again after the call finishes. Built-in tools, `UseSolutionAsync`, and plugin tool calls that reach their solution through the plugin context all resolve through this one path; no ASP.NET middleware or invocation wrapper participates. A failed call still counts as use: resolving the instance indicates the user still wishes to use that solution.

A plugin tool that never resolves the solution it was given does not keep it alive. `UseSolutionAsync` is the means by which a plugin declares and refreshes its interest. get_solution_status does not resolve an instance and therefore never updates the time: listing solutions must not keep them alive.

## Compilation and diagnostics

The host creates an ISolutionContext for the snapshot passed to each `UseSolutionAsync` callback. It exposes these members:

```csharp

public interface ISolutionContext
{
    Solution Solution { get; }

    ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default);
}
```

Plugins use `context.Solution` to access projects and request semantic compilations on demand with `Project.GetCompilationAsync`.

`GetDiagnosticsAsync` returns compiler, analyzer and Razor diagnostics of every severity, including hidden diagnostics, for `context.Solution`. Plugins filter the returned diagnostics themselves.

`ISolutionContext.GetDiagnosticsAsync` and the built-in `get_diagnostics` tool use the same diagnostics pipeline defined in [#202](https://github.com/mrpmorris/Roslynk/issues/202). The pipeline computes once per snapshot and always includes analyzers; `includeAnalyzers` only filters the response. For the same solution snapshot, whichever caller arrives first starts the computation and caches its `Task`; the other caller awaits that exact same `Task`, whether it is still running or has already completed. A plugin cannot opt out of the analyzer-inclusive computation — its diagnostics cost the same as `get_diagnostics`' — and the built-in tool applies its output filters after awaiting the shared task; plugin requests join it with no separate queue or priority.

If an edit publishes a new snapshot during the callback, the context still provides diagnostics for the original snapshot. The snapshot check described in Solution resolution (a reference comparison against the instance's published model, performed after the callback returns) retries the callback with a context for the new snapshot; a callback may therefore run more than once per call.

## Trust model and the read-only contract

Plugins are trusted code. Installing one is choosing to run it: a plugin is an arbitrary .NET assembly executing inside the daemon process with the user's full permissions — it can touch the filesystem, the network and anything else the user can. Roslynk applies no sandbox, and `plugin add` deploys the DLL it is given without inspecting its contents (no probe, by design); provenance is the user's responsibility.

What the contract guarantees is about the surface, not the sandbox. Plugins cannot call Roslynk tools — none of them, modifying state or not (see Non-Goals) — and are fed interfaces with read-only properties, immutable Roslyn snapshots, and host-owned interfaces; what they receive exposes no way to alter Roslynk state — the one host-side effect the contract lets a plugin cause is `UseSolutionAsync`'s load-on-demand (see Solution last-used tracking). The compile-time contract and the loader's guards exist for correctness, not security.

## Tool discovery

Plugin tool methods use the MCP SDK's `[McpServerTool]` attribute.

Roslynk inspects the concrete type of the plugin instance returned by the factory.

For each discovered tool method, it creates an McpServerTool bound to that same initialized instance.

If two discovered methods in the same plugin produce the same exposed tool name — ordinary C# overloads share a method name, so discovery must resolve it — the first in discovery order wins: the second is recorded as an OpenTelemetry error naming both methods and is ignored. The plugin runs with the surviving tool.

The SDK supplies tool descriptions, parameter schemas, argument binding and result conversion.

Discovery covers public methods, including public methods inherited from base types; non-public methods are never discovered.

## Tool signature convention

Every plugin tool method declares, by analyzer enforcement:

1. `string solutionId` as its first parameter.
2. A `CancellationToken` parameter.

The solutionId binds normally through the MCP SDK as a real, published parameter. The host does not rewrite the published metadata to inject or remove it.

The token the host passes is the request's cancellation token linked with the host-shutdown token, so either an aborted request or a shutting-down host cancels the in-flight tool call. Cancellation is not mapped to a retryable failure shape: the invocation wrapper rethrows OperationCanceledException exactly as it does for built-in tools, and the MCP transport reports the cancelled call. An informational OpenTelemetry record accompanies each cancelled plugin tool call naming the token's source (request abort or host shutdown). Shutdown cancelling in-flight tool calls (see Shutdown) is this same shutdown token flowing through the link, not a separate mechanism.

No per-call response budget reaches a plugin tool. `multi_query` — the built-in batching tool that executes several read-only operations in one request under per-call budget slices — does not accept plugin tools (see Result size).

## Result size

Plugin tool results are bounded by the process-wide response budget (`Roslynk:MaxResponseChars`, default 80,000 characters) by the same wrapper backstop that applies to built-in tools that cannot page: results exceeding the budget are truncated after the last whole line and marked `outputTruncated` on an otherwise non-error result. The process-wide budget value is available to the plugin on `IPluginContext` at initialization, so a plugin can size its own results before the backstop cuts them.

No per-call budget slice is passed to a plugin tool (see Tool signature convention).

## Tool naming

Roslynk prefixes every plugin tool name:

```text

{normalized plugin name}__{declared tool name}
```

Examples:

| Plugin folder | Declared tool name | Exposed tool name |
|---|---|---|
| Plugin1 | tool_1 | plugin1__tool_1 |
| Plugin2 | some_other_tool | plugin2__some_other_tool |

There are no rules on declared tool names — no length cap, no character-set restriction, no ban on embedded `__`. The MCP harness prefixes every tool with a unique server name (`mcp__{server}__{tool}` is prepended by harnesses, never by Roslynk), so the server/tool boundary is unambiguous above the exposed name, and an embedded `__` in an exposed name cannot be confused with the harness's separator.

The accepted residual risk: a client that enforces stricter limits on tool names than Roslynk does — for example rejecting names outside `^[a-zA-Z0-9_-]{1,64}$` — can refuse the whole tool surface, affecting every plugin and the built-in tools, with no Roslynk-side record. Nothing validates declared names on any path — the analyzer checks tool signatures, never tool names, and `plugin add` checks folder and DLL naming only — so any deployment, added through the CLI or copied by hand, can produce a nonconforming exposed name.

Plugin-to-built-in collisions are structurally impossible: built-in tool names never contain `__`, so the mandatory plugin prefix cannot collide with one. Plugin-to-plugin collisions cannot occur: each normalized plugin name is registered once. A duplicate exposed name can only arise within one plugin (two methods sharing a declared name): discovery keeps the first and ignores the second, recording an OpenTelemetry error naming both methods (see Tool discovery).

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

The wrapper performs no last-used tracking: the plugin reaches its solution through `IPluginContext.UseSolutionAsync`, and the registry path that resolves the instance updates the last-used time (see Solution last-used tracking).

Registration associates each full exposed tool name with its target method and plugin instance.

Request cancellation reaches the tool method's CancellationToken parameter.

Roslynk may invoke a plugin's tool methods concurrently: multiple tool calls can be in flight against the same plugin instance at once, and a tool call can run while the delivery pump is invoking one of the plugin's OnNext callbacks. The host applies no synchronization around the plugin instance; state shared across calls must be thread-safe. The contract's only concurrency guarantee is the negative one from Event delivery: OnNext is never concurrent with itself.

## Shutdown

Roslynk:

1. Stops accepting new plugin tool calls.
2. Signals shutdown cancellation on every in-flight plugin tool call's token, and waits for those calls to finish. A tool that ignores cancellation stalls shutdown until the process is killed — the mirror of the accepted delivery stall; accepted.
3. Unloads solutions as part of shutdown; the unloading publishes no events — Evicted marks idle eviction, which cannot happen during shutdown. A `UseSolutionAsync` call that arrives after this step does not reload: it returns Faulted without retry.
4. Ends event delivery: publication sources are quiesced, then OnCompleted is enqueued as the final item on the delivery queue — after all pending events, it is every plugin's final callback. The host waits for the pump to deliver every pending event and OnCompleted before exiting. A handler (including OnCompleted itself) that never returns stalls this wait — the mirror of the accepted delivery and tool-call stalls; accepted.
5. Exits the process, reclaiming everything the plugins hold.

Process exit reclaims AssemblyLoadContexts, retained snapshots and everything the plugins hold (see Plugin contract).

## API compatibility

The contract surface never breaks.

`IPlugin`, `IPluginFactory` and `PluginFactoryAttribute<T>` are fixed; changes to their shape do not ship. If a change ever demands it, new type names are added alongside the old (an `IPlugin2`-style pattern, as the Windows API does) and existing plugins keep compiling and loading unchanged; that decision, if it ever arises, is made then.

`IPluginContext`, `ISolutionContext`, `ISolutionEvent` and `IUseSolutionResult<T>` are implemented only by the host. Adding members to them is compatible with existing compiled plugins: old plugins never see the new members until they are recompiled. Removing, renaming or changing existing members is a breaking change and is not done.

The two plugin-facing enums follow the additive rule: members may be added, and plugins must tolerate unknown values — an exhaustive switch over a contract enum must include a default arm.

There is no version metadata to validate and no package surface validation. No build-time machinery enforces the policy: an accidental breaking change would surface as deployed plugins failing their type guards or factory invocation — recorded as OpenTelemetry errors, those plugins not used. The risk is accepted.

## Remaining design details

- Whether plugin tools join `multi_query` later (excluded for now; joining requires opening the closed read-only operations catalog, a read-only gate on qualifying tools, and a pinned-snapshot hand-off so batch slots share one generation).
- Whether to deploy plugins without stopping the daemon, via shadow-copied plugin DLLs (deferred; the stop is accepted for now).
