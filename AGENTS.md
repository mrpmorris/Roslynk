# Working on Roslynk

This is a source-based implementation guide for future changes. Paths below are relative to the repository root unless a section says otherwise. Re-check the affected implementation before changing it; this guide describes the architecture and its current limits, not a substitute for the source.

## Start here

- The solution is `Source/Morris.Roslynk.slnx` (not under `Source/App`).
- Read `git status` before editing and preserve unrelated work.
- When Roslynk is connected, use its semantic tools for compiled C#, Razor and CSHTML: open the solution, use `get_members`/`get_symbol_body` to inspect implementations, and use reference/caller queries for impact analysis. Follow `skills/roslynk/SKILL.md` and `skills/roslynk/references/tools.md` for tool usage. Retry `Indexing` while loading; do not manually reload for ordinary source edits.
- Batch related read-only questions with `multi_query`. Re-query after writes. A running Roslynk daemon can inspect edited source, but editing this repository does **not** replace the daemon's executing implementation; run tests against the checkout to validate new behavior.
- Use normal file tooling for non-source configuration, documentation, and new-file creation. Roslynk's patch tool only edits existing files, and its plain-text fallback is rooted at the solution directory (`Source`), not the repository root.
- The most important seams are `RoslynInstance`, `SolutionModel`, `ApplyPipeline`, `AtomicFileWriter`, `SolutionFileSync`, and the internal cores used by `MultiQueryCatalog`.

## Required workflow for feature and tool changes

- When creating a new read-only tool, assess whether it is a candidate for `multi_query` and **ask the user whether to include it**. Do not silently include or exclude it. Continue independent implementation work while that choice is pending; if included, implement the pinned-model core, catalog/enum integration and relevant tests described below.
- Add every new feature and feature change to the `# Unreleased` section at the top of `releases.md`. Create that section at the top if it is missing, preserving existing release history.
- Always keep consumer-facing skill and tool documentation synchronized with tool definitions and their descriptions, including parameters, defaults, output, errors, supported scope and batch availability. In this repository the files are `skills/roslynk/SKILL.md` (singular `SKILL.md`, the consumer skill file) and `skills/roslynk/references/tools.md`. Update both as applicable to the changed contract; checking their consistency is part of completing every tool change. Keep any additional consumer `SKILLS.md`/`tools.md` copies synchronized if introduced later.

## Repository map and build conventions

| Location | Responsibility |
| --- | --- |
| `Source/App/Morris.Roslynk` | Engine, feature tools, shared infrastructure, DI and MCP tool registration. |
| `Source/App/Morris.Roslynk.Mcp` | ASP.NET Core host, loopback HTTP transport, stdio bridge/daemon startup, idle eviction and OpenTelemetry integration. Ships as the `Roslynk` .NET tool, command `roslynk`. |
| `Source/App/Morris.Roslynk.AppHost` | Aspire development host. |
| `Source/App/Morris.Roslynk.Tests` | Engine, feature, concurrency, file synchronization and writing tests. |
| `Source/App/Morris.Roslynk.McpTests` | Host composition, published MCP schemas, tool invocation and transport-facing tests. |
| `Source/TestFixtures` | Small independent solutions: Simple, Broken, References, Conditional, Razor, Cshtml, LocalFunction, CodeStyle and Generator. |
| `skills/roslynk` | User-facing agent skill and detailed tool contracts. Update when tool behavior changes. |
| `README.md`, `releases.md` | Setup/tool guidance and release history. |

`Source/Directory.Build.props` sets .NET 10, latest C#, nullable and implicit usings, and warnings as errors. Package versions are central in `Source/Directory.Packages.props`; do not add local versions to individual project references. `Source/.editorconfig` specifies tabs (width 4), Allman braces, file-scoped namespaces, CRLF for C#/VB, and usings outside namespaces. Existing classes commonly use PascalCase private fields and explicit constructor injection; follow nearby code without unrelated formatting churn.

Feature tools live in `Features/<Area>/<Operation>/<Operation>Tool.cs` (MultiQuery is a flatter slice). Shared mechanisms live under `Infrastructure`. Namespace structure follows directories. Both test assemblies have access to core internals via `InternalsVisibleTo`.

## Lifecycle, snapshots and ordering

Sources: `Infrastructure/Lifecycle/{InstanceRegistry,RoslynInstance,SolutionModel,WriteResult,SemaphoreSlimReadWriteLock}.cs` under the core project.

`InstanceRegistry` owns a concurrent dictionary of lazy `RoslynInstance`s keyed by normalized solution paths (`SolutionKey`). Key comparison is case-insensitive on Windows/macOS, ordinal on other platforms. Do not invent a second registry or cache keyed by an unnormalized path.

- `GetOrBegin` supports nonblocking open: create/start the initial load, or start a dirty rebuild while returning the instance.
- `GetOrBeginAsync` is the normal data-tool entry point. It joins/starts a dirty rebuild, but does not wait for the first-ever load. A tool must handle a null `model.Solution` as `Indexing`.
- `GetOrAddAsync` waits for initial readiness and has a different dirty path that closes/recreates the instance; it is useful in tests, not interchangeable with the normal data-tool path.
- Instances track access for idle eviction and own the workspace, watcher, queue and cancellation lifetime.

`SolutionModel` is a published immutable wrapper containing status, optional Roslyn `Solution`, fault message and immutable project metadata. Each new wrapper has a fresh GUID `Id`, even when it contains the same `Solution`. This is a **publication generation**, not a content hash or Roslyn document version. Diagnostics status transitions can also change it.

`RoslynInstance.CurrentModel` uses `Volatile.Read`; publication uses `Volatile.Write`. `ReadModelAsync` briefly acquires a read lock, captures one model, and releases the lock. A reader starting during a locked write waits for publication, then performs semantic work against its captured immutable snapshot without holding the lock. Do not repeatedly consult `CurrentModel` halfway through a logical read or combine symbols/trees from different solutions.

Writes and diagnostics run on an unbounded channel with one consumer:

1. `EnqueueWriteAsync` queues a transform receiving the latest `Solution` when it executes.
2. Under the write lock, `RunWriteAsync` publishes `Updating`, invokes the transform, then publishes `WriteResult.Updated` as `Ready`.
3. It marks diagnostics needed and clears the diagnostics cache. On transform failure it republishes the previous solution and faults that work item's completion; subsequent work must still run.
4. `WriteResult` contains the updated solution and changed physical paths. Watcher folds use an empty path list because disk was already edited.

Diagnostics requests are queued after pending writes. Compilation runs against a captured solution without holding the read lock, so semantic readers can continue. The cache is keyed by diagnostics mode and invalidated by writes/rebuilds. `DiagnosticsResult` includes the solution actually compiled: use that solution for mapping diagnostics, not whichever solution happens to be current later.

Rebuilds load a replacement workspace in the background. `RebuildGate` and `RebuildInFlight` coalesce callers. **Both successful rebuild publication and failure publication go through the same channel as incremental writes**, with the write lock protecting the swap. Preserve this ordering; direct publication can race a queued fold and lose an update. Rebuild completion reattaches watchers and disposes the previous workspace.

### Snapshot details to verify when extending lifecycle behavior

- `CurrentModel` access itself does not wait for an active writer; `ReadModelAsync` is the fenced read API. Several existing write tools capture `CurrentModel` directly.
- `ProjectModels` is populated on workspace load/rebuild, but helpers such as `Ready(solution)`, `Loading(solution)` and `Updating(solution)` do not carry it forward. `AdvanceTo` currently uses `Ready(solution)`. Do not assume project metadata survives every publication; changes needing it should explicitly address preservation and tests.
- A failed operation may publish a new model ID even though its source content did not change. Do not use generation equality as text equality.

## Disk writes: preserve all layers of protection

Sources: `Infrastructure/Writing` and `Features/Patching/ApplyPatch/ApplyPatchTool.cs`.

Semantic edits normally compute an immutable updated solution and call `ApplyPipeline.ApplyAsync`:

- The pipeline enqueues the operation on `RoslynInstance`, so validation and persistence execute under the single writer.
- The transform receives the latest solution, but `updated` was computed by the caller. Pass the snapshot it was computed from as `ApplyAsync(instance, updated, basedOn: solution, ...)`: inside the queue, `RebaseAsync` replays only the documents `updated` changed relative to `basedOn` onto the latest `current`, and throws `StaleWriteException` if an intervening publication (watcher fold or another write) changed any of those documents. Intervening edits to other documents are kept, not reverted. Without `basedOn` (the older overload), `updated` is persisted as-is and can silently revert an intervening publication. New write tools must pass `basedOn` and test the intervening-edit case.
- `BuildWritesAsync` enumerates changed ordinary and additional documents. Every physical path is written once, even if multiple target frameworks/projects share it. The computation must therefore update every document sharing that path consistently.
- Before any file is written, each loaded text is compared with current disk text using `FileHash` (SHA-256). A mismatch throws `StaleWriteException`. Additional documents, including Razor source, receive this check too.
- Generated paths ending in `.g.cs` are excluded from persistence. This is a broad suffix rule, not just a Razor check.
- `GetChangedFilePaths` is the preview seam. It does not perform the complete disk stale validation. `checkOnly` must not publish or write changes.
- The pipeline covers changed documents, not arbitrary project/file additions or removals. Do not assume all Roslyn `SolutionChanges` can be persisted through it.

`AtomicFileWriter` stages the whole batch into sibling `<path>.roslynk.tmp` files, then commits each with `File.Replace(temp, target, <path>.roslynk.bak)`. If a replacement fails, it attempts to restore already committed files from backups. Commit cleanup attempts to delete temp/backup siblings. Preserve the two public suffix constants because the watcher filters them.

Be precise about the guarantee: individual replacements are atomic, and the code implements rollback across a batch; this is not a filesystem transaction visible atomically to external readers or crash recovery. Staging happens before the commit try/finally, so a staging failure/cancellation can leave temp files. Rollback can itself fail, and there is no cross-process lock between disk validation and replacement. Do not strengthen these claims in tool documentation without changing implementation and testing failure cases.

Encoding is path-specific: `AtomicFileWriter` accepts an explicit encoding and defaults to UTF-8 without BOM. `ApplyPipeline` currently creates `PendingWrite` without carrying the document encoding; `ApplyPatchTool` explicitly preserves recognized UTF-8/UTF-16 BOM encodings. Do not assume all write tools preserve encoding identically.

### Patch-specific path

`ApplyPatchTool` uses `UnifiedDiffParser` and `PatchApplier`, then queues its own transform using `AtomicFileWriter` rather than `ApplyPipeline`. It computes hunks against **current disk text**, with optional `baseVersions` checked using `FileHash.Of(text)`. Thus an omitted version is not equivalent to the semantic pipeline's loaded-text guard.

Hunks are content-anchored and must match uniquely; line numbers are not authoritative. Existing text files are supported; creation/deletion and binary content are rejected. Model paths are resolved before the solution-contained plain-text fallback, so inspect resolution carefully for linked files. Plain-text fallback rejects `bin`/`obj` and paths outside the solution directory. Encoding detection and path/version normalization are part of the contract.

After persistence, it updates all matching ordinary/additional documents in the latest solution. Plain files are disk-only. Build inputs and direct Razor patches can still require the watcher's dirty rebuild to refresh derived state.

### Existing guard limitations

Most write tools (`apply_code_action`, `apply_code_fix`, `extract_method`, `change_signature`, `rename_parameter`, `remove_unused_usings`) pass `basedOn`. `rename_symbol` still calls the overload without it, so there the disk guard alone does not prove that its precomputed solution was derived from the latest in-memory model. Also, `StaleWriteException` is an ordinary exception: the general MCP wrapper maps uncaught exceptions to `Faulted`; it does not automatically translate this type to `Stale`. When adding a write tool, deliberately format its stale failure and test the public response. Do not copy an older caller assuming it exercises every protection.

## File watching and freshness

Sources: `Infrastructure/Watching/{SolutionFileWatcher,SolutionFileSync}.cs`.

`SolutionFileWatcher` debounces events for 250 ms, batches distinct paths, and forwards both old and new paths for renames. Project directories are watched recursively; linked source/additional files and tracked ancestor build files can add shallow watches outside them. Watchers are reconstructed after a reload.

`SolutionFileSync` owns the classification rules:

- Ignore `bin`, `obj`, `.roslynk.tmp` and `.roslynk.bak` centrally, including direct calls that bypass the watcher adapter.
- Known `.cs` edits fold into all matching documents via the write queue; unchanged text is ignored, avoiding feedback from the server's own writes. Text folds request background compiler diagnostics.
- Known deletions remove documents. New `.cs` files can be folded into owning default-glob projects; explicit compile-item cases mark the instance dirty for MSBuild reevaluation.
- Default-glob detection currently uses project-file text heuristics, not full MSBuild item evaluation. Treat unusual conditions, exclusions and nested projects carefully.
- A `.cs` path registered as an analyzer additional file is a rebuild input, even if also compiled. Do not accidentally fold it as ordinary C# only.
- Build files (`.csproj`, `.vbproj`, `.fsproj`, `.props`, `.targets`, `.sln`, `.slnx`, `.editorconfig`) use content baselines to distinguish edits from touches.
- Other nonignored files mark the instance dirty, covering Razor, resources and generator inputs. Dirty rebuild is lazy and coalesced on subsequent use.

Watcher exception handling is best-effort; disk stale validation is a separate correctness layer. Fold transforms use the latest solution, but the current text-fold implementation reads disk text before queueing; do not assume it re-reads inside the queue. For concurrency work, test intervening edits and rebuilds explicitly.

## Semantic identity, conditional code and Razor

`SymbolResolver`, `SymbolSignature`, `SymbolAmbiguity` and `EnclosingDeclaration` centralize names, overload parsing, candidate rendering and enclosing declaration identity. Use them instead of ad hoc `ToDisplayString()` identities or string matching. Candidate strings must round-trip into the same tool and resolve the intended overload. The signature renderer progressively adds ref kinds and qualified parameter types when needed; parameter names/defaults and nullable spellings are handled by the parser. Preserve overload, generic, indexer and partial-declaration tests.

Local functions are named as members of their declaring member: `N.Outer.Type.Method.local`, and `N.T.M.outer.inner` for nested local functions. Roslyn gives them no qualified display name (`ToDisplayString()` is the bare `local`; the documentation ID flattens to `M:N.T.local`), so never use either as identity. `LocalFunctions` owns the rules:

- `NamedContainer` walks past lambdas/anonymous methods and maps accessors to their property/event. `ContainerChain` gives the outer-to-inner containers for outline nesting.
- `SymbolResolver.FullyQualifiedName` and `SymbolSignature.Of` build the name from that container. `Of` always renders the container's own parameter list (`N.T.M(int).local(string)`), so `KeyOf` and candidates stay unique across overloaded containers and round-trip.
- Any segment of a query may carry a parameter list. `SymbolSignature.TryGetContainer` splits at the last top-level dot, and `NameMatches` matches a local function's container recursively as a name in its own right, not by flattened text.
- Local functions are not in `SymbolFinder`'s declaration index. `FindByFullyQualifiedNameAsync` searches for them only when no ordinary symbol matched: it resolves the container name recursively, then scans that container's syntax (`FindInAsync`). A bare name scans the whole solution (`FindAllAsync`). This fallback is sound because C# forbids a nested type and a member of the same name in one type (CS0102), so a dotted name cannot mean both. Keep the fallback lazy: it walks syntax.
- `SymbolKindText` reports `localfunction`. `SymbolPlacement`, `EnclosingDeclaration` and `get_members` nest local functions under their containers.

Regression tests: `Morris.Roslynk.Tests/Features/LocalFunctions` against `TestFixtures/LocalFunctionSolution` (nested class, local-in-local, overloaded container).

`ProjectionService.BuildAsync` creates the base solution plus variants toggling each condition symbol that is uniformly defined or uniformly undefined across C# projects. Symbols with mixed definitions across loaded projects are skipped. **This is not exhaustive enumeration of combinations**; a branch needing multiple simultaneous toggles may remain uncovered. Resolve/query within each projection's own solution, then deduplicate using `ProjectionService.KeyOf` (the shared fully qualified signature with ref kinds). This key intentionally collapses identical qualified signatures across projects; it is not assembly-qualified identity.

`RenameSymbolTool` is a useful example of multi-projection editing: run semantic rename per projection, collect/deduplicate physical-file text changes by span, then apply the union to every matching document in the base solution. Preserve inactive-branch and multi-target consistency when adding similar operations.

Razor has two representations: real `.razor`/`.cshtml` additional documents and generated C# made available as editable model documents by `RazorDocumentGenerator`.

- Workspace loading remaps analyzer references to a shadow-copy loader **before** requesting compilations/Razor augmentation. Otherwise generator DLLs in another project's `bin/obj` can be locked and prevent rebuilding them.
- Razor augmentation prefers a fresh pre-generated snapshot, otherwise runs a discovered SDK generator. Snapshot analysis checks missing/new sources, timestamps, directive files and orphan outputs, including flat and folder-preserved layouts.
- If no generator is available, it may keep stale but nonorphan generated files to preserve useful binding. Freshness is therefore not guaranteed just because generated documents exist.
- Once augmentation supplies generated documents, it removes the native Razor generator reference to prevent duplicate partial members and bindings to an immutable duplicate.
- Use `RazorMapping.GetDisplaySpan` for user-facing source positions. `RazorChangeMapper` maps pre-edit generated spans through `#line` directives, obtains Razor text from the captured solution, validates bounds and exact old text, and rejects unmappable/mismatched changes before writes.
- Rename keeps generated C# changes in memory while writing the corresponding additional documents. One generated document can map to several Razor files (including imports); duplicate/conflicting mapped edits need handling. Never write generated C# to disk as the rename result.
- Other tools that edit or take positions in `.razor`/`.cshtml` go through two seams:
  - `RazorSourceDocument` resolves a user path to the generated document and maps Razor positions into it. Each mapped position is verified by mapping it forward again. Action IDs store the Razor path, because generated paths mix separators.
  - `RazorGeneratedChangeFolder.FoldAsync` writes the result back region by region, not edit by edit, because formatting operations re-indent to the generated class and move whitespace across `#line` boundaries. Text outside regions may differ only in whitespace, otherwise the fold throws. Lines Roslyn formatted (space-indented) are re-based onto the file's indentation, and untouched lines are kept verbatim. The in-memory generated regions are rewritten to the reconciled Razor text, so a following operation still maps before the watcher rebuilds.
- Add new Razor-capable write tools through these two seams, and map `RazorMappingException` with `RazorGeneratedChangeFolder.ErrorFor`.
- Analyzers skip generated code, and the compiler never reports CS8019 there. `RazorUnusedUsings` therefore re-parses the generated document as ordinary code to find unused `@using` lines, and never edits `_Imports.razor`/`_ViewImports.cshtml`.
- Regression tests: `Features/RazorSupport` against `RazorSolution` and `CshtmlSolution`.

## Diagnostics and code actions

`DiagnosticsService` gets project compilations and optionally runs analyzers through `AnalyzerDriverFactory`. Source generators can affect compilation even when analyzer diagnostics are disabled. Preserve `project.AnalyzerOptions` (additional files/editorconfig) and isolation of analyzer failures.

`GetDiagnosticsTool` defaults to analyzers enabled, whereas the lower-level service defaults to false. Counts for errors/warnings/info/hidden are always returned; flags control details. It uses the queued diagnostics API and maps results against `DiagnosticsResult.Solution`. Private fixer-trigger IDs are removed from both counts and rendered diagnostics.

`DocumentDiagnosticsProvider` is the shared code-action diagnostic path. It obtains compiler diagnostics from the whole compilation, then filters to the document (needed for completion diagnostics such as CS8019). Analyzer work is narrowed to fixable, nonsuppressed rules, including hidden rules. It has a five-second analyzer cancellation budget and compiler-only fallback, a four-document task cache keyed by document ID and dependent semantic version, and one reusable analyzer driver keyed by project/version/analyzer set. Shared computation deliberately does not take a caller's cancellation token. Keep caches bounded and semantic-version-aware.

`CodeActionCatalog` discovers C# fix/refactoring providers from Roslyn feature assemblies; providers requiring unavailable imports are skipped. It maps the private unnecessary-imports trigger to public IDE0005. `CodeActionService` discovers actions and encodes path/span/kind/key into an opaque base64 JSON ID. Applying an ID re-discovers the action; do not retain Roslyn action objects across calls or assume discovery remains valid after an edit. Only an `ApplyChangesOperation` produces the changed solution for persistence. Check neighboring behavior when introducing support for a new provider or operation type.

## MCP contracts and adding tools

Engine registration is `ServicesRegistration.AddRoslynk` in `_ServicesRegistration.cs`; services are currently singletons. Public tool classes carry `[McpServerToolType]`, with named methods, explicit tool annotations and parameter descriptions. Add shared dependencies to this registration and preserve thread safety for singleton state.

Hosts use `.WithRoslynkTools()`, **not** bare `.WithToolsFromAssembly()`. The wrapper clones published tool definitions, moves JSON Schema defaults into descriptions, and formats escaped exceptions consistently. Binding errors become `Invalid`, other escaped exceptions become `Faulted`, and cancellation is rethrown. Schema behavior is tested through the published MCP surface, not just reflection on method signatures.

Output is a compact text protocol: `key=value` headers, optional blank line and tab-indented outline. Booleans are `Y`/`N`; `status` is omitted for Ready. Use `OutlineBuilder`, `OutlineError`, `ChangedFilesOutline`, `FolderFiles`, `SymbolNode`, `ProjectName` and `SolutionRelativePath` as appropriate. Sanitize header/free-text record fields so embedded newlines cannot corrupt framing. Source bodies returned verbatim must retain their original formatting. Tool descriptions sometimes show `#project`/`#path`; current `OutlineBuilder.Header` actually emits unprefixed keys. Trust implementation and contract tests when changing output.

For a read-only feature, follow the public-method/internal-core split exemplified by `GetSymbolTool`: public method gets the instance and captures the model once; the core accepts that model and uses it throughout. Do not reacquire current state from inside a core.

For a new read-only tool, first ask the user whether to include it in `multi_query`, as required above. If included, update `MultiQueryOp` (in `MultiQueryOperation.cs`) and `MultiQueryCatalog.Entries` together, using the tool's name constant and internal core. Public/core bindable parameter names and defaults must agree. The binder supplies `SolutionModel`, `RoslynInstance` and cancellation itself; callers cannot inject them. Unknown keys fail rather than being silently ignored. Its supported scalar coercions are string/bool/int; extend binder and schema tests deliberately if adding other types.

`MultiQueryTool` captures one model for every slot. It executes independent slots in order and isolates ordinary exceptions per slot. Current limits are 25 operations and 200,000 accumulated body characters; the output limit is checked between slots, so one complete slot can overshoot it. Skipped slots are whole `Truncated` results. A random boundary frames verbatim bodies, and delimiter integrity is checked. Continuations use `expectSnapshot` to reject a different publication generation before any operation executes. Diagnostics, lifecycle, writes and code-action discovery are not in the current batch catalog.

When extending a tool: preserve the response/error contract, add meaningful behavior tests, add schema/composition tests for public changes, keep the consumer skill and tool reference aligned with definitions/descriptions, update README where affected, and record the feature change under `# Unreleased` at the top of `releases.md`. A similar existing tool is a starting point, not proof that it handles every concurrency or Razor case.

## Testing and useful regression locations

Tests use xUnit and descriptive `When..._Then...` names. `TestSolutions` lazily restores read-only fixtures. Write tests must use its `CreateScratch...` helpers (source-only temporary copies, excluding `bin/obj`) or their own isolated fixture. Do not mutate committed fixtures during tests. Generator fixtures explicitly build their analyzer project before loading because MSBuildWorkspace's design-time load does not build the generator DLL. `TestServices` centralizes code-action helper composition.

| Change area | Start with tests under `Source/App` |
| --- | --- |
| Queue, locks, diagnostics ordering/cache | `Morris.Roslynk.Tests/Infrastructure/Lifecycle` (including `RoslynInstanceTests` and read/write lock tests) |
| Persistence/stale behavior | `Morris.Roslynk.Tests/Infrastructure/Writing`, plus patch, rename and code-action feature tests |
| Watcher classification/self-write filtering | `Morris.Roslynk.Tests/Infrastructure/Watching/SolutionFileSyncTests.cs` |
| Batch snapshot pinning, framing, limits/binding | `Morris.Roslynk.Tests/Features/MultiQuery` |
| Name identity/overload candidate round trips | `Morris.Roslynk.Tests/Infrastructure/Resolution`, `Infrastructure/Projections/ProjectionServiceKeyOfTests.cs` |
| Conditional semantic behavior | `Morris.Roslynk.Tests/Features/ConditionalBranchCoverageTests.cs`, conditional reference/rename tests |
| Razor mapping, stale generated snapshots | `Morris.Roslynk.Tests/Infrastructure/Razor`, `Features/References/RenameSymbolTests/RazorRenameTests.cs` |
| Analyzer isolation/cache/fix discovery | `Morris.Roslynk.Tests/Infrastructure/Diagnostics`, `Infrastructure/CodeActions`, `Features/Diagnostics` |
| Tool registration/schema/wire behavior | `Morris.Roslynk.McpTests/Server` and `Morris.Roslynk.Tests/Server/ToolCompositionTests.cs` |

Run focused tests for the affected behavior, and use Roslynk diagnostics during source editing. Compilation diagnostics do not replace behavioral tests, real builds needed for generator fixtures, or release packaging checks. From the repository root:

```powershell
dotnet test Source/App/Morris.Roslynk.Tests/Morris.Roslynk.Tests.csproj --filter FullyQualifiedName~ApplyPipelineTests
dotnet test Source/App/Morris.Roslynk.McpTests/Morris.Roslynk.McpTests.csproj
dotnet test Source/Morris.Roslynk.slnx --configuration Release
```

For a write feature, cover preview/no disk change, successful disk plus model update, external edit rejection, queued snapshot change, and shared physical paths where relevant. Concurrency tests should use controlled tasks/signals and bounded waits rather than timing guesses. The existing atomic-writer test checks successful batch content/cleanup; it is not evidence of coverage for all rollback, staging or crash scenarios.

## Hosting and release touchpoints

HTTP is restricted to loopback by `LoopbackOnlyExtensions`, default port 6502 (`Roslynk:Port`). The stdio command bridges to the shared HTTP daemon; it is not a second engine with its own workspace registry. `DaemonLauncher` probes/starts that daemon so loaded workspaces can outlive a client. Keep protocol stdout free of ordinary logs. Startup, tracing and observability changes belong in the MCP host; semantic behavior belongs in the core.

The release workflow `.github/workflows/workflow.yml` triggers on numeric version tags (no `v` prefix), runs the full solution tests in Release on Linux, packs the MCP project using the tag version, and publishes to NuGet via OIDC. Preserve cross-platform file/path behavior even when developing on Windows. Ordinary feature implementation does not require starting a daemon, publishing a package or tagging a release.
