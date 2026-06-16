# Roslynk — .NET/Roslyn MCP server (Prioritized Build Plan)

> **Name:** *Roslynk* = **Roslyn + link** — the link between Claude and Roslyn. Package id prefix `Morris.Roslynk`; the tray app's display name is **Roslynk**.

This is the same scope as the original TODO, reordered by **priority** rather than by topic.
Ordering is driven by two things:

1. **Hard dependencies** — Tier 0 must exist before anything else can.
2. **Observed payoff** — derived from analysing real C# sessions (WinForms→Blazor port,
   Blazor web app, a Fody-weaver project, a MAUI/Unity app). The dominant costs were:
   * **Build-error loops** — 37 and 47 `dotnet build`/test invocations in two sessions, used purely as "did my edit compile?" checks, plus terminal-output grepping.
   * **Hand-rolled renames** — a 15-file field rename done by text edits *shipped a build break and silently skipped the `.razor.cs` partial-class files*; a 58-file namespace rename via `string.Replace` introduced a base-class collision caught only by accident.
   * **Whole-file / guessed-offset reads** to locate a member before editing — the most pervasive tax, present in every session.
   * **Multi-file signature cascades** (EventCallback / record changes) redone by hand, sometimes across multiple retried sessions.

   **Negative evidence:** across 250+ `.cs` edits, inheritance trees, call graphs, callees, and
   `find_implementations` barely appeared. They are deliberately demoted to Tier 3.

> **Recommended first shippable slice = Tier 0 + Tier 1.** Writes go through `apply_patch`
> (content-anchored git diff) only; the char-span editor (`apply_document_edits`) was considered and
> **dropped** — offsets go stale and are computed poorly by an LLM, and the patch path covers the same
> need more safely.

> **Scope — C# only.** The MCP operates exclusively on `.cs` compiled in the loaded solution. It does
> not read, search, or edit any other file type (`.razor`, `.json`, `.csproj`, Markdown, loose/uncompiled
> `.cs`, …) — those are the host's job via its own file tools, which the MCP would only duplicate. Any
> request against a target outside this scope returns **Not Supported** (see Structured errors), never a
> guess or a bare Not Found. The sole "outside the solution" exception is *reading* referenced-assembly
> symbols as metadata (§2.7).

---

## Tier 0 — Foundation (prerequisite for everything)

### Project shape

> **Fine-grained by default — not one big assembly or a handful of god-classes.** Follow the usual
> decomposition: **one public type per file**, small single-responsibility files, and tools organised as
> **vertical slices** (a folder per tool/use-case holding its handler + mapper + response). Each layer is
> its own project, and the **host (entry/transport) and the Application layer are kept separate** — the
> host holds no application logic.

> **Tech baseline:** **.NET 10**, C# latest, **Windows-only** (ships `win-x64`). The `Mcp` project uses **Minimal APIs** (no MVC controllers). Official `ModelContextProtocol` C# SDK for the protocol; xUnit + built-in `Assert` for tests; Central Package Management + `Directory.Build.props` for shared settings (`TreatWarningsAsErrors`, SourceLink, etc.).

* [ ] One project per layer; keep each small and single-purpose.
* [ ] **Contracts** — DTOs only (tool request/response, source span, provenance, diagnostics, error envelope).
* [ ] **Core** — Roslyn code intelligence (workspace, symbol resolution, diagnostics, references, rename / code-actions, dead-code). No MCP or UI concerns.
* [ ] **Application (AppLayer)** — orchestration: tool handlers as vertical slices, lifecycle & instance management (the session-keyed singleton + eviction), the apply protocol, observability wiring. References Core; knows nothing of transport or UI.
* [ ] **Mcp** — the **Windows Service host**: Minimal-API HTTP/SSE endpoints + MCP protocol mapping + Windows Service hosting (Automatic start; also runs as a console app for dev) + composition root. Thin — delegates all real work to Application; contains no application logic. **No UI.**
* [ ] One test project per production project, mirroring it (`*.CoreTests`, `*.ApplicationTests`, …); integration tests run against sample `.sln`/`.slnx` fixtures (see Testing).

```text
Morris.Roslynk.Contracts     // DTOs
Morris.Roslynk.Core          // Roslyn code intelligence (engine)
Morris.Roslynk.Application   // tool handlers (vertical slices), lifecycle, apply protocol, OTel
Morris.Roslynk.Mcp           // Windows Service host: Minimal-API/SSE + MCP transport + hosting (no UI)
Morris.Roslynk.*Tests        // one per project: CoreTests, ApplicationTests, McpTests, ...
```

### MSBuild registration + solution loading

* [ ] Register MSBuild before creating the workspace via `MSBuildLocator.RegisterDefaults`, guarded by `MSBuildLocator.IsRegistered`.
* [ ] Resolve and **test** the MSBuild location strategy for the SDK-only / no-Visual-Studio box in CI from day one — this is the #1 thing that breaks these servers on a fresh machine.
* [ ] Use `MSBuildWorkspace`.
* [ ] Implement `open_solution(path)`; accept and validate **both `.sln` and `.slnx`** (the newer XML solution format) — the path must exist and be one of those two extensions.
* [ ] Ensure the Roslyn / MSBuild version is new enough to parse `.slnx` (recent SDK / VS 17.10+); surface a clear load error if an `.slnx` is opened against a toolchain too old to understand it.
* [ ] Load the solution once as the initial full load; apply incremental document updates afterward (see File watching).
* [ ] Subscribe to `WorkspaceFailed` and include partial project load failures in load diagnostics.
* [ ] Store the current `Workspace`, `Solution`, `snapshotId`, and `indexVersion`.
* [ ] Return projects, target frameworks, document count, and load diagnostics.
* [ ] Record per-project target frameworks and the conditional-compilation symbols per TFM (see Multi-targeting in cross-cutting) — a multi-targeted project is several compilations, not one.
* [ ] Report initial load progress; a full load can take tens of seconds on large solutions.

### Symbol identity

* [ ] Use Roslyn documentation comment IDs for declarations.
* [ ] Prefix IDs with project identity.
* [ ] Include overload signatures and generic arity / type parameters.
* [ ] Never rely on simple name alone.
* [ ] Store enough data to re-resolve symbols in the current snapshot.
* [ ] Handle symbols with no documentation comment ID (local functions, lambdas, anonymous types, function pointers); `GetDocumentationCommentId` can return null.
* [ ] Fallback identity = containing member ID plus **ordinal within parent** (stable across reformatting), not source location.

```text
App|T:Sales.CustomerService
App|M:Sales.CustomerService.GetAsync(System.Int32)
App|M:Sales.CustomerService.GetAsync(System.String)
```

### Symbol resolution (accepting imperfect inputs)

> The model rarely produces an exact doc-comment ID. Every symbol-taking tool resolves **three**
> input forms, tried in order, so a near-miss still lands instead of failing.

* [ ] **Exact doc-comment ID** (the canonical identity above) — used as-is.
* [ ] **Fuzzy / partial FQN** — tiered scoring with ranked candidates: exact (1.0) → case-insensitive (0.99) → constructor/parameter-list omitted (~0.9) → generic-arity / type-args omitted (~0.8) → `.`-vs-`+` nested-type normalization (~0.75); accept above a threshold (~0.7). When more than one survives, return them as **disambiguation candidates** (each with full ID, kind, project, location) rather than silently picking one.
* [ ] **Position** `(filePath, line, column)` — `SymbolFinder.FindSymbolAtPositionAsync`; the host often has a cursor location, not an ID.
* [ ] Resolution is one shared helper (`resolve_symbol`) reused by §1.2 navigation, §1.3 references/rename, and §2.x — not reimplemented per tool. It returns either a single resolved symbol or a ranked candidate list (an **ambiguous** Not Found sub-case, per Structured errors).

### Source spans

* [ ] Return both char spans and line/column spans.
* [ ] Use Roslyn `TextSpan.Start`, `TextSpan.End`, and `TextSpan.Length`.
* [ ] Treat `endChar` as exclusive.
* [ ] Include document version with all spans.

```json
{
  "documentId": "...",
  "sourcePath": "...",        // see Source location & provenance
  "sourceType": "source",
  "documentVersion": 17,
  "startChar": 1200,
  "endChar": 1450,
  "length": 250,
  "startLine": 42,
  "startColumn": 9,
  "endLine": 50,
  "endColumn": 5
}
```

### Source location & provenance

> Every read that carries a location uses `sourcePath` + `sourceType` instead of a bare file path, so
> editability and origin are never guessed from the path. The MCP's writable surface is C# (regular
> Roslyn `Document`s); everything else is read-only.

* [ ] `sourceType` ∈ `source` | `generated` | `decompiled` | `sourcelink` | `metadata`.
* [ ] Editability rule: **only `source` is writable** (a regular working-tree `Document` — includes `*.Designer.cs`). All other types are read-only; the write tools enforce this at the boundary (see §1.4), not on trust.
* [ ] `source` — in-solution C# (incl. `*.Designer.cs`); `sourcePath` is the working-tree file. Optional `designerManaged: true` advisory on designer-owned files (the edit persists, but the visual designer may rewrite it on round-trip).
* [ ] `generated` — a Roslyn `SourceGeneratedDocument` / build output (incl. `*.razor.g.cs`); read-only (rebuilt). When it maps back to an authored origin, attach `generatedFrom: { path, line }` (e.g. the `.razor` markup) — the place to edit the usual way. Razor is surfaced this way, **not** as its own source type.
* [ ] `decompiled` — metadata-as-source pseudo-file; read-only and **non-literal** (synthesized, lowered); `sourcePath` may be a pseudo-path or null.
* [ ] `sourcelink` — real origin source fetched via SourceLink; read-only but literal; carry `uri` + `commit`.
* [ ] `metadata` — signatures / XML docs only, no body; `sourcePath` is null.
* [ ] Literalness is derivable from `sourceType` (only `decompiled` is non-literal); the `documentation` object keeps its own `isLiteralSourceText` because it is separately normalized (see §2.5).

### DTO baseline + version stamping

* [ ] Return compact DTOs; never expose raw Roslyn objects.
* [ ] Include `snapshotId` and `indexVersion` in every response; include `documentVersion` wherever source spans are returned.
* [ ] Include `isPartial`, `accessibility`, `kind`, containing type/namespace, and the `sourcePath`/`sourceType` provenance (see Source location & provenance — `sourceType` subsumes the old `isGenerated` boolean).
* [ ] Add `maxResults` to all list-style tools as a page-size limit, paired with the shared `cursor` continuation convention (see Response size & pagination) rather than truncating with no way to resume.

### Lifecycle & instance management

> The server is a long-lived host (HTTP transport) that can hold **several solutions at once**. One Roslyn
> workspace is built per solution, shared by everyone using that solution, and torn down once no one is.
> This is the answer to "single vs multi-solution": **multi-solution by isolation** — N independent
> instances keyed by path, no merged cross-solution index.

* [ ] Keep a process-singleton `ConcurrentDictionary<string, RoslynInstance>` keyed by the **normalized absolute solution path** (case-insensitive — Windows-only); a `.sln` and a `.slnx` for the same logical solution are distinct keys.
* [ ] `open_solution(path)` returns a **`solutionId` handle**; all later calls pass `solutionId` (not the long path again) to route to the right instance. Two callers opening the same path share the same instance and id.
* [ ] A `RoslynInstance` owns: the `MSBuildWorkspace` + current `Solution` snapshot, `snapshotId` / `indexVersion`, its file watcher, the single-writer lock, and the set of active sessions referencing it. It is an **in-process object graph, not a child process** — eviction disposes the workspace and watcher (MSBuild may still own separate build-node processes); Roslyn re-compiles diffs incrementally off the swapped snapshot, so there is no manual "compile the differences" step.
* [ ] **Reference-count by MCP session, not by raw HTTP/TCP connection.** A streamable-HTTP MCP session (`Mcp-Session-Id`) spans many connections — SSE streams reconnect and retry — so tie lifetime to the session, and add an **idle grace timer** before eviction: reloading a large solution costs tens of seconds, so do not tear down the instant the last socket blips. Evict when no session has referenced the instance for the grace window, or on explicit `close_solution`.
* [ ] Guard first load against races: concurrent `open_solution` for the same key must load **once** (e.g. `Lazy<Task<RoslynInstance>>` or a per-key semaphore), with all callers awaiting the same load.
* [ ] Funnel every mutation for an instance through its single-writer lock as an **array of edits applied as one batch** → one new snapshot, one recompile, one `snapshotId` / `indexVersion` bump (no interleaved partial states, no N recompiles; see §1.4).
* [ ] The watcher and the write tools share the instance: suppress self-induced writes (see File watching) so an apply does not race its own watcher event; external edits bump the version and feed the self-healing stale-write path (§1.4).
* [ ] `get_solution_status`, `reload_solution`, `close_solution`, and `clear_cache` all operate per `solutionId`. Optionally cap concurrent instances / evict LRU under memory pressure — each instance is a full workspace, and overlapping solutions duplicate their shared projects.
* [ ] Define tool behavior **during** the initial load / re-index window (see Load & readiness in cross-cutting): return a typed `indexing` / `notReady` status with progress rather than blocking indefinitely or failing opaquely.
* [ ] **Accepted scope limit — refactor completeness, not file sync:** file *content* stays consistent across instances automatically (a write through one instance is picked up by every other instance's watcher for the same path). What does **not** propagate is a semantic decision: `find_references` / `rename_symbol` see only the deciding instance's reference graph, so references that live in a project belonging to *another* solution are missed — a rename through S1 won't touch callers in a project that exists only in S2, leaving S2 broken with no file-change for its watcher to react to. The host must drive each solution it cares about for a global refactor. State this on `find_references` / `rename_symbol`.
* [ ] **Cross-instance same-file write race:** the per-instance single-writer lock does **not** serialize two *different* instances writing the same physical file. The self-healing stale-write check (§1.4) covers the interleaved case (the watcher invalidates the other instance's snapshot mid-apply), but genuinely concurrent writes to one path would otherwise need a process-global per-path write lock. **Decided: accept last-writer-wins + watcher reconciliation** (no process-global lock) — cross-solution same-file writing is the host's to avoid (see the matching accepted limit in §1.4).

---

## Tier 1 — Highest payoff (build immediately after foundation)

> These four items address every dominant cost above: the build loops, the member-hunting reads,
> the dangerous renames, and unsafe writes.

### 1.1 Diagnostics — the fast post-edit gate (biggest single win)

* [ ] In-process semantic compile check (`GetCompilationAsync` + emit diagnostics) as the **default** post-edit gate, scoped to **changed documents only** so it stays fast enough to run after every edit.
* [ ] Run Roslyn **analyzers** alongside compiler diagnostics (`CompilationWithAnalyzers`) so analyzer/IDE diagnostics — and the code fixes in §2.4 — are available; run them incrementally / scoped to changed documents, the way Visual Studio does, to keep the gate fast.
* [ ] Source analyzers from the **project's own referenced analyzer packages** (the analyzer assemblies it already builds with), loaded at runtime — not a fixed built-in set — so the diagnostics and fixes match exactly what Visual Studio / `dotnet build` would report for that project.
* [ ] `get_diagnostics(projectId, documentId?, severities?)` — a **single** call covering both errors and warnings; do not split into separate error/warning tools (severity is one axis of variation, not a different operation).
* [ ] `severities` is an optional filter (e.g. `["error","warning"]`, or a `minSeverity`); **default returns errors + warnings**.
* [ ] Callable cold (before any edit) to triage an inherited/pre-existing diagnostic state, not only as a post-edit gate.
* [ ] Return diagnostics with file path, span, severity, ID, and message; sort/group errors before warnings so one call gives an errors-first view.
* [ ] **Always** include per-severity counts (e.g. `{ "errors": 3, "warnings": 40, "hidden": 12 }`) even when the list is filtered or capped, so filtering/truncation is never silent.
* [ ] Include before/after diagnostic counts after writes.

### 1.2 Symbol read / navigation (replaces whole-file & guessed-offset reads)

* [ ] `search_symbols(query, kind, project, namespace, maxResults)`
* [ ] `get_symbol(symbolId)`
* [ ] `get_type(typeId)`
* [ ] `get_members(typeId, includePrivate, includeInherited, nameFilter, include* kind toggles)`
* [ ] `get_source_span(symbolId)`
* [ ] `find_definition(...)` — resolve a usage to its declaration; accepts a `(filePath, line, column)` position as readily as a symbol reference (the host frequently has a location, not an ID). Named to match the `find_references` / `find_implementations` family.
* [ ] `find_implementations(symbolId)` — implementations of an interface / abstract member, overrides, and derived types (`SymbolFinder.FindImplementationsAsync` / `FindDerivedClassesAsync`).
* [ ] `get_type_hierarchy(typeId)` — base-type chain, implemented interfaces, and known derived types in one view.
* [ ] Every symbol-taking tool here accepts **any** of the three identifier forms from *Symbol resolution* (doc-comment ID | fuzzy FQN | position) via the shared `resolve_symbol` helper — the model rarely has an exact ID.
* [ ] Make `get_symbol`/`get_method` "fat" — fold source span + signature + accessibility + containing type into the one call, so a single round-trip is enough to act.
* [x] **Source location instead of a body payload; `get_method` removed.** A type's members are inspected via `get_members`, which returns each member's *metadata* (kind, accessibility, signature) plus its source location (`sourcePath`, 1-based `startLine` and `endLine`); the caller reads the source from the host's file tool over that span. The separate `get_method` tool and the earlier `includeBody` / `MethodDto.Body` payload were removed: returning locations keeps responses cheap and avoids duplicating source into the model's context. Do not reintroduce a body payload on `get_symbol` / `get_type` either.
* [ ] Keep list-style tools (`search_symbols`, `get_members`) lean; defer documentation enrichment to Tier 2.

### 1.3 References + rename (the highest-value refactor — and the one that caused real bugs by hand)

* [ ] `find_references(symbolId)`.
* [ ] Define pagination/cap **now** (per Response size & pagination): return `truncated: true` with a `cursor`, and allow filtering by project/namespace (e.g. `ILogger` returns thousands).
* [ ] Default-filter references in build-generated code (`SourceGeneratedDocument`s / `.g.cs`) so results aren't drowned in noise — but do **not** filter `*.Designer.cs` (it is ordinary `source`, not generated). Surface references in generated Razor at their mapped `.razor` location via `generatedFrom` (see §2.6), rather than dropping them.
* [ ] `rename_symbol(symbolId, newName)` — Roslyn-semantic and atomic across the whole C# surface, **including partial classes and `.razor.cs` code-behind**, touching neither string literals nor comments.
* [ ] `rename_symbol` does **not** rewrite `.razor`/`.cshtml` markup (outside the MCP's write scope); it must **report** every markup reference it left untouched as `generatedFrom` pointers, so they can be fixed the usual way — never skip them silently.
* [ ] `rename_symbol` **refuses** when the new name would collide with or shadow an existing symbol (Roslyn detects these): return a conflict result listing the clashes — never apply a colliding rename.

### 1.4 Write path + AI safety

**Tool.** `apply_patch(patch, baseVersions, checkOnly)` — a git unified diff, plus the version each touched file was based on (see *Versioning*). `checkOnly` previews without writing.

**Versioning — the content hash is the source of truth.**

* [ ] A file's version is the **hash of its bytes on disk**, not a counter the MCP maintains. A made-up counter only moves when the MCP *notices* a change (via the watcher), so it silently lies when the coder edits externally and the event is missed/coalesced; a hash changes by itself whenever anyone edits. `documentVersion` is backed by this hash.
* [ ] Keep `snapshotId` / `indexVersion` as cheap labels for "did anything change" fast-paths and response stamping — but the **authoritative pre-write check re-hashes the real file on disk**. Never trust the watcher to have kept the model current.

**Apply protocol — the safe write sequence.**

* [ ] **1. Stage:** apply the patch into a **temp copy** of each target file (`git apply` / `git apply --check`). This is conflict-atomic — if any hunk doesn't line up, nothing real is touched. For `checkOnly`, stop here and return the preview. Also define behavior when the tree isn't a git repo or `git apply` can't run (fall back to applying the diff in-memory, since we already hold each file's content + hash).
* [ ] **2. Lock:** take the **per-instance single-writer lock** so the watcher or a second patch can't move things mid-apply.
* [ ] **3. Re-check:** **re-hash each target file on disk** and compare to the client's `baseVersions`. If any differ, the coder (or another tool) edited since the read → reject with a **self-healing** stale result: current version + current span/text, so the model re-bases just those files in one step rather than re-reading everything.
* [ ] **4. Commit:** write each file with **`File.Replace`** from its temp copy — atomic per file, no torn reads (a reader sees the whole old or whole new file, never half). Pass a **backup path** so each replaced file's prior bytes are retained; if any file in the batch fails to replace, **restore the already-swapped files from their backups** so the batch is all-or-nothing *within the process* (a hard crash mid-restore still needs the deferred journal — see limits).
* [ ] **5. Resync:** refresh the workspace from disk (disk first, then workspace), suppress the self-induced watcher events so the apply doesn't double-trigger, re-index changed documents, release the lock, and return changed files + diagnostics.

* [ ] **One commit path for all writes — but two ways the change is produced.** `apply_patch` takes a client diff (validated via `git apply`, guarded by client `baseVersions`). `rename_symbol` / code actions are **Roslyn refactors**: `Renamer.RenameSymbolAsync` (or the code action) returns a **new immutable `Solution`** computed against the current snapshot — no incoming diff, no client `baseVersions`. We read the **changed documents' new text** out of that new Solution. Both then persist through the **same** lock → re-hash → `File.Replace` → resync sequence (for rename, "stage to temp" = write the new document text to a temp file). There is exactly one place that touches disk.
* [ ] **Do not persist refactors via `workspace.TryApplyChanges`** — it writes files through Roslyn's own text writers, bypassing our hash re-check and atomic `File.Replace`. Drive the disk write ourselves from the new Solution's document texts, then advance the in-memory workspace to that Solution.
* [ ] **Preview (`checkOnly`) on every write tool, not just `apply_patch`.** `rename_symbol`, `change_signature`, and code actions all accept `checkOnly`: run the same resolution / conflict / version checks and return the would-be diff (changed files + spans) without touching disk. The model can always look before it leaps.

**Deliberately *not* in v1 — accepted limits.**

* [ ] **No OS file locks.** The .NET convention is detect-and-reload, not lock (VS et al. don't lock files); the hash re-check + atomic replace cover the coder safely without making the MCP the one actor whose presence breaks other tools' saves.
* [ ] **No *crash*-atomic journal.** Step 4's backup-restore gives all-or-nothing for in-process failures (a failed `File.Replace` rolls the batch back), but there is **no** atomic multi-file replace on Windows (Transactional NTFS is deprecated), so a hard crash *between* replaces can still leave a partial commit — visible and recoverable via the working tree's git. Add a journal later only if that proves to be a real problem.
* [ ] **No cross-solution same-file coordination.** If two open solutions share a physical `.cs` and both write it, it's last-writer-wins — the host's problem to avoid, not something v1 guards with a process-global lock.
* [ ] **Cannot see an editor's unsaved buffers.** The pre-write hash check reads the file *on disk*, so if a coder has unsaved changes open (e.g. in VS), the MCP writes against the on-disk version and the editor raises its normal "file changed on disk" conflict on next save. Accepted and relied upon (detect-and-reload). A live `VisualStudioWorkspace` would avoid this but requires being a VS extension — out of scope.
* [ ] Treat `MSBuildWorkspace` snapshots as immutable; apply external edits through the workspace API or by re-opening the document rather than mutating in place.
* [ ] Add limits to source returned per call; avoid tools that return the entire solution unless explicitly requested.
* [ ] Enforce the write boundary on `sourceType`: every write tool (`apply_patch`, `rename_symbol`, code actions) refuses any target whose `sourceType` is not `source`, returning **Not Supported** (see Structured errors). Do not rely on the model honoring a read-side flag.
* [ ] `apply_patch` is `.cs`-only: reject (Not Supported) any patch hunk targeting a file that is not solution-compiled `.cs` (e.g. `.razor`, `.json`, `.csproj`) — those are edited by the host, not the MCP.

---

## Tier 2 — Strong payoff

### 2.1 Signature changes (recurring multi-file cascades)

* [ ] `change_signature(methodId, newSignature)` with automatic call-site updates — collapses the repeated EventCallback / record-parameter cascades that were redone by hand across files and even across sessions.

### 2.2 Callers

* [ ] `get_callers(methodId)`.
* [ ] Consider a depth parameter, or a `get_call_graph(methodId, depth)`, if multi-level tracing is expected.

### 2.3 Full build / test (slower, out-of-process)

* [removed] `build_solution` — removed. It shelled out to the same `dotnet build` the host can run directly and returned only a lossy parsed summary (counts plus the first error lines). `get_diagnostics` covers the fast in-process compile check; a raw `dotnet build` covers full verification with no fidelity loss.

### 2.4 Mechanical fixes

* [ ] Depends on the analyzer + code-fix layer enabled in §1.1 (decision: run analyzers, VS-style) — in scope for v1.
* [ ] `get_code_actions(documentId, span)` to discover refactorings and fixes at a position.
* [ ] `apply_code_action(actionId, checkOnly)` with a preview diff.
* [ ] `apply_code_fix(diagnosticId, fixId)` (covers only fixes attached to a diagnostic; diagnostic-free refactorings need the code-action surface above).
* [ ] Organize / remove unused usings (via `apply_code_fix` on `IDE0005`, or as a code action) — recurring manual cleanup after moves and renames.

### 2.5 Documentation enrichment (for the single-symbol detail tools)

* [ ] Include resolved structured `documentation` on `get_symbol`/`get_type`/`get_method`; omit it, or return only a truncated one-line summary, on list-style tools to keep discovery calls lean.
* [ ] Parse `GetDocumentationCommentXml` into a shallow object (`summary`, `params`, `returns`, `remarks`, `exceptions`), normalizing inline tags to markdown (`<see cref>` → simple name in backticks, `<paramref>`, `<c>`/`<code>`); do not nest further XML/JSON inside each field.
* [ ] When a member resolves through `<inheritdoc/>`, return the inherited documentation text by default and set `source` to `inherited`.
* [ ] Populate `inheritedFrom` with the base symbol ID plus its `sourcePath`/`sourceType` location (see Source location & provenance): a working-tree path when `source`, or the assembly name when `metadata`/`decompiled`/`sourcelink`.
* [ ] Keep `isLiteralSourceText` false for inherited (and all normalized) docs.
* [ ] Define the `documentation` object: `summary`, `params`, `returns`, `remarks`, `exceptions`, plus provenance fields `source` (`own` | `inherited` | `none`), `inheritedFrom` (null, or an object carrying the base symbol ID and — when in this solution — file path and source span; assembly name when external), and `isLiteralSourceText`.
* [ ] Treat the `documentation` field as a read-only derived view (normalized, possibly inherited from another file); never compute edits from it. Editing a doc comment must first fetch the actual source span text, then patch against that.

### 2.6 Razor / Blazor (read-only context — the MCP does not write Razor)

> Scope: the MCP's semantic surface is C#. Razor/cshtml are read for *context* and edited the usual
> way with normal file tools — grepping `.razor` markup for handler/`EventCallback` wiring was the most
> repeated search in the Blazor sessions, so the value is in *finding* those sites, not patching them
> here. Roslyn does **not** parse `.razor` directly, but the Razor SDK source generator emits C#
> (`*.razor.g.cs`) into the compilation, so the generated code is reachable for reads.

* [ ] Include Razor-generated documents in the semantic model (`GetSourceGeneratedDocumentsAsync`); confirm `MSBuildWorkspace` is configured to run source generators so they are present.
* [ ] `find_references` / `get_callers` include hits inside generated Razor documents so `.razor` binding sites (`@onclick`, `EventCallback` wiring, `@ref`) are found.
* [ ] Map each generated hit back to the original `.razor` markup via the generator's `#line` directives, and return it as `sourceType: "generated"` with `generatedFrom: { path, line }` pointing at the markup — i.e. "edit here, the usual way."
* [ ] The MCP does not write `.razor`/`.cshtml`. `rename_symbol` and code actions skip markup and **report** the affected `generatedFrom` sites for manual editing (see §1.3); C# (incl. `.razor.cs` code-behind) is unaffected and remains Tier 1.

### 2.7 Referenced-assembly (metadata) reads

> Referenced assemblies (BCL, NuGet) are *not* in the solution but their symbols are still reachable as
> Roslyn metadata — all read-only (`sourceType` ≠ `source`). This is the **only** "outside the solution"
> case the MCP answers: loose/uncompiled `.cs` and non-`.cs` files are **Not Supported** (the host's job).
> The workspace can hold several solutions at once (multi-solution by isolation — see Lifecycle).

* [ ] Resolve symbols in referenced assemblies via Roslyn metadata symbols: `get_type` / `get_members` / `get_symbol` return signatures, accessibility, and XML docs even with no source, marked `sourceType: "metadata"` with assembly name + version. Native Roslyn capability — commit to this layer.
* [ ] `find_references` into a metadata symbol is one-directional (references *from* solution code into it, not the assembly's own internal callers) — state that rather than implying completeness.
* [ ] v1 is **metadata-only** (signatures + XML docs). `decompiled` (ICSharpCode.Decompiler / metadata-as-source) and `sourcelink` (origin source + `uri`/`commit`) are **deferred** — their `sourceType` values stay reserved so they can be added later without reshaping the DTO. They'd be genuinely additive: with the file-fallback out of scope, this MCP is the only way Claude could see *inside* a dependency.

### 2.8 Dead-code detection

> The one capability the surveyed servers had that we didn't (sailro). High value for "clean this up"
> tasks — but only if it doesn't lie: most "unused" C# is actually reached indirectly.

* [ ] `find_dead_code(scope, includePublic, maxResults)` — symbols with no references, **conservatively filtered** to suppress false positives.
* [ ] Exclude by construction: test members (xUnit / NUnit / MSTest attributes), generated code (`*.g.cs`, `*.Designer.cs`, anything under `obj/` / `bin/`, `[GeneratedCode]` / `[CompilerGenerated]`), and DI / reflection-activated members (`[Export]` / MEF, attribute-registered entry points, and the public API surface when `includePublic` is false).
* [ ] Treat as *live* anything reachable non-lexically: interface implementations, `virtual` / `abstract` / `override` chains, and **Razor / XAML-referenced** members — resolve `.razor` binding sites via the generated-document mapping (§2.6) so handlers wired only in markup are not reported as dead.
* [ ] Report each candidate with a **confidence** and the reason it's suspected (no refs found / only refs are tests) — never a bare "delete this"; the host decides.

---

## Tier 3 — Lower frequency / nice to have

> Negative evidence: across the analysed sessions these almost never came up. Build them only when
> a concrete need appears, not for completeness.

* [ ] `get_inheritance_tree(typeId)`
* [ ] `find_implementations(symbolId)`
* [ ] `get_callees(methodId)` / `get_call_graph(methodId, depth)`
* [ ] `get_document(documentId)`
* [ ] `format_document(documentId)`
* [ ] `move_type(typeId, targetNamespace)` — or route through the code-action surface.
* [ ] `run_tests` — separate slower tool that shells out to `dotnet`.

### Char-span edit API — dropped

* [ ] `apply_document_edits` is **not** built: char-span offsets go stale and are computed poorly by an LLM, and `apply_patch` (content-anchored git diff) covers the same need more safely. Decided against; revisit only if a concrete need appears that patch cannot serve.

### Non-C# / non-solution files — out of scope (deliberately)

* [ ] The MCP provides **no** text search, file reading, or patching for non-`.cs` files, loose/uncompiled `.cs`, or anything outside the loaded solution. The host already has Read/Grep/Edit for those; the MCP would only duplicate them.
* [ ] Semantic tools return **Not Supported** for such targets (see Structured errors), so the caller uses its own file tools instead of reading the result as "doesn't exist."
* [ ] `.razor`/`.cshtml` are still *readable for C# context* via their generated documents (§2.6), but are edited by the host, not the MCP.

---

## Cross-cutting — build incrementally alongside the tiers

### File watching

> Tier 1 can rely on re-index-after-own-write while the MCP is the sole writer; full watching matters
> once external edits (the user's IDE, git operations) must be reflected.

* [ ] Watch `.cs`, `.csproj`, `.sln`, `Directory.Build.props`, `Directory.Build.targets`, `global.json`.
* [ ] Debounce file events.
* [ ] For `.cs` changes, update the affected document; for project/solution/build-file changes, reload the affected project or whole solution.
* [ ] Increment `snapshotId` and `indexVersion` after changes.
* [ ] Ignore self-induced file changes from write tools so an apply does not double-trigger a reload.

### Performance

* [ ] Build the initial symbol index after solution load.
* [ ] Cache documents, symbols, and member summaries.
* [ ] Avoid eager semantic model creation for every document; lazily compute expensive data; cache reference searches if needed.
* [ ] Add cancellation tokens and timeouts for expensive operations.
* [ ] Add index status / progress reporting.
* [ ] **No cross-restart index persistence in v1**: rebuild the index on each launch and accept the cold-start cost (reported via §0 load progress); add disk caching only if cold-start proves painful — it brings invalidation complexity.

### Multi-targeting (TFM)

> The MAUI projects multi-target (e.g. `net8.0-android`, `net8.0-windows`, `net8.0-ios`); each is a
> separate Roslyn compilation with different `#if` symbols, so the *same* query can have *different*
> answers per TFM. This is the default state for this stack, not an edge case.

* [ ] Treat each target framework as its own compilation; never assume a single one.
* [ ] Add an optional `targetFramework` parameter to symbol/reference/diagnostic tools (`get_symbol`, `get_members`, `find_references`, `get_diagnostics`, …).
* [ ] Define the default when `targetFramework` is omitted (e.g. the first / declared-primary TFM) and **state which TFM a response reflects** in the DTO.
* [ ] Make conditional-compilation differences explicit: a symbol present only under `#if WINDOWS` does not exist in the Android compilation — report that, rather than silently returning "not found".

### Response size & pagination

* [ ] One continuation convention across all list-style tools: `maxResults` sets the page size, and a `cursor` / `continuationToken` (plus `truncated`) resumes — so "there's more" is always recoverable, never a silent ad-hoc cut.
* [ ] Per-call byte/token caps on source-bearing responses (`get_document`, `get_source_span`, `find_references`) — these land in the model's context window, so cap and paginate rather than dumping.
* [ ] When a response is capped, return counts plus the cursor to fetch the rest; never truncate silently.
* [ ] **Strip leading indentation** from returned source (recording the common indent removed, so it stays reversible) — roughly ~10% fewer tokens on deeply-nested C# at no information loss.
* [ ] **Adaptive detail degradation** instead of hard truncation on structural tools (`get_members`, project/type overviews): when a response would exceed the cap, first reduce depth / drop non-public members / collapse bodies — and say so in the response — before falling back to pagination.

### Structured errors

* [ ] Every tool returns a resolution status: **OK** (resolved/answered), **Not Found** (target could be in scope but doesn't exist — e.g. a symbol ID that no longer resolves), or **Not Supported** (target outside the C#/solution scope — non-`.cs`, uncompiled, or out of solution; carry a `reason` distinguishing not-in-solution vs not-C#).
* [ ] One error envelope for every tool: `code`, `message`, `currentSnapshotId`, optional `details`.
* [ ] **Not Found** vs **Not Supported** is load-bearing: Not Found = genuinely absent (don't look elsewhere); Not Supported = use the host's file tools / open the right solution.
* [ ] Keep the **operational** statuses as a separate axis (not resolution outcomes): **stale-snapshot** (write against an old version — return the current version to re-base, per §1.4) and **not-ready/indexing** (during load, per Load & readiness). Ambiguous-symbol stays a Not Found sub-case with candidates.
* [ ] For the stale / unresolvable-symbol case, the typed error carries the current `snapshotId` and, where possible, a closest-match suggestion.

### Tool metadata & annotations

* [ ] Tag every tool with MCP annotation hints — `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint` — so the host treats reads, mutations, and re-runs correctly (reads need no confirmation; `apply_patch` / `rename_symbol` / code actions are destructive). Reads (`get_*`, `search_symbols`, `find_references`, `find_dead_code`) are read-only + idempotent.
* [ ] Keep tool **descriptions** task-oriented and say when to prefer a semantic tool over the host's raw file read/edit — so the model reaches for `get_symbol` / `apply_patch` instead of grepping and hand-patching `.cs`.

### Load & readiness

* [ ] While the solution is loading or re-indexing, tool calls return a typed `indexing` / `notReady` status with progress (percent or phase), not an indefinite block or an opaque failure.
* [ ] Reads may be served from a partial index when safe; otherwise return `notReady` with a phase/ETA so the caller can decide to wait or proceed.

### Observability (OpenTelemetry, session-keyed)

> Monitoring is first-class: the server is instrumented with OpenTelemetry and **exports OTLP to a
> backend the user configures** (Honeycomb, a self-run Aspire dashboard, any OTLP collector). Roslynk
> ships **no** dashboard or telemetry UI of its own — it is an OTLP *producer* only — so activity is
> watched **per MCP session** in whatever backend you point it at.

* [ ] **OTel baked in (not optional):** instrument with the **OpenTelemetry .NET SDK** — the first-party Microsoft / .NET observability libraries (`OpenTelemetry.Extensions.Hosting` + `OpenTelemetry.Instrumentation.AspNetCore` + the **OTLP exporter**; add `Microsoft.Extensions.Telemetry` for enrichment if useful). `ActivitySource` for spans, `Meter` for metrics, the OTel logging provider for logs. **Let the library own the connection** — its OTLP exporter handles batching, retry, back-off, and reconnect; we write no transport/connection code, we just point it at an endpoint.
* [ ] **Key everything on the MCP session id** (`Mcp-Session-Id` — the same id Lifecycle ref-counts by): attach it as an attribute/tag on every span, metric, and log (`mcp.session.id`) and as a log scope, so the dashboard can filter and follow a single session end-to-end. Use it as a span/log/metric *attribute* (not a per-session OTel *resource*, which would need a tracer-provider per session — note that as the heavier option only if true per-session dashboard "resources" are ever wanted).
* [ ] **Also tag with solution, project, and file** wherever the operation has them, so telemetry drills down session → solution → project → file: `roslynk.solution` (the `solutionId` / solution name), `roslynk.project` (project name, plus TFM where relevant), and `roslynk.relative_file_path` (path **relative to the solution root** — never absolute: readable, stable, and doesn't leak machine layout). Put all of these on **spans and log scope**. For **metrics**, use `roslynk.solution` and `roslynk.project` as dimensions but **keep `relative_file_path` off metric tags** — per-file cardinality would explode the metric series; rely on traces/logs (and exemplars) for file-level drill-down.
* [ ] **Record the operation name and its arguments on every tool span:** `roslynk.operation` (the MCP tool name, e.g. `rename_symbol`) and each argument as `roslynk.arg.<name>`. Scalar args verbatim (`symbolId`, `newName`, `severities`, `targetFramework`, `maxResults`, `checkOnly`, …); for large or source-bearing args (a patch body, document text) record a **summary — byte length + content hash, not the full blob** — to keep spans lean (the apply protocol already tracks content by hash anyway). The outcome status and any error code go on the same span.
* [ ] **Span the operations that matter:** solution load (timing/phase), each tool call (tool name, `solutionId`, `snapshotId`, outcome status), diagnostics runs, and the apply protocol (stage → lock → re-check → commit, tagged with the result: applied / stale-rejected / rolled-back) and instance eviction. Nest tool spans under the session.
* [ ] **Metrics:** tool-call counts and latencies by tool + outcome; active sessions; loaded solutions/instances; apply success/stale/rollback counts; load/index durations.
* [ ] **No dashboard or UI shipped — export to a configured OTLP endpoint.** A settings file holds the OTLP **endpoint URL**, **protocol** (`grpc` or `http/protobuf`), and any **headers** (e.g. Honeycomb's `x-honeycomb-team` API key). Point it at Honeycomb, a self-run Aspire dashboard, an OpenTelemetry Collector — anything that speaks OTLP. Configure it the standard way — the `OTEL_EXPORTER_OTLP_*` environment variables or `appsettings` — and the SDK's exporter reads those and manages the connection itself. Telemetry is sent **outbound** to that endpoint (which may be remote, e.g. Honeycomb) — distinct from the MCP server's own listener, which stays loopback-only (see Packaging). If no endpoint is configured, telemetry simply isn't exported and the server still runs.

### Testing (required — full unit coverage)

> Every tool and every safety guarantee in this spec must be covered by automated tests; the
> write-safety and resolution logic especially are too subtle to ship untested.

* [ ] **Test fixtures = real sample solutions** checked into the test project: a multi-targeted (TFM) project, a Blazor/Razor project (with `*.razor` + `*.razor.cs` + generated `*.razor.g.cs`), a project with `*.Designer.cs`, one referencing a NuGet/BCL metadata symbol, and both a `.sln` and a `.slnx`. These exercise the provenance, multi-targeting, and Razor paths against actual Roslyn compilations rather than mocks.
* [ ] **Per-tool unit tests** for every tool (diagnostics, navigation, references, rename, code actions, dead-code, documentation) — happy path plus the Not Found / Not Supported / ambiguous outcomes.
* [ ] **Symbol resolution tests** — each fuzzy-scoring tier, position-based resolution, and the ambiguous-candidates path (more than one match → ranked candidates, not a silent pick).
* [ ] **Write-safety tests** (the highest-risk area): stale-hash rejection with self-healing payload; the single-writer lock serialising concurrent applies; atomic `File.Replace`; and **mid-batch failure → backup-restore rollback leaves every file at its original content** (assert all-or-nothing within the process).
* [ ] **Write-boundary tests** — every write tool refuses a non-`source` `sourceType` and a non-`.cs` patch hunk with **Not Supported**; assert the boundary holds even when the model "asks nicely."
* [ ] **Pagination / structured-error / readiness tests** — cursor round-trips never lose or duplicate items; per-severity counts present when capped; calls during load return `notReady`.
* [ ] **Loopback-only test** — the MCP server refuses a non-local connection (it's the only inbound listener; telemetry export is outbound to the configured endpoint).
* [ ] **Host lifecycle tests** — the service runs headless and keeps sessions alive; a **service stop** (SCM stop / `sc stop`, or Ctrl+C in console) triggers graceful shutdown that disposes every instance + file watcher and flushes telemetry.
* [ ] **Telemetry tests** — spans, metrics, and logs all carry `mcp.session.id`; apply-protocol spans record the correct outcome (applied / stale-rejected / rolled-back); with no OTLP endpoint configured the server still starts and runs (export simply disabled).
* [ ] Run the suite in CI on the SDK-only / no-Visual-Studio box (per §0) so MSBuild resolution is tested on a fresh machine, not just a dev box.

---

## Packaging & host — Windows service, no UI (last)

> The host is a **headless Windows service** that **starts automatically with the OS** and runs the HTTP
> MCP server. **No UI** — the developer configures it through a **config file** (and/or environment
> variables). Transport is **HTTP only** (see Lifecycle & instance management): the shared multi-solution
> singleton, session ref-counting, and warm cross-client workspaces only pay off on a persistent host.
> Not a tray app, not a stdio desktop extension.

* [ ] Build the `Mcp` project as a **Windows Service** (`Microsoft.Extensions.Hosting.WindowsServices`), installed with **Automatic start** so it **launches with Windows at boot** (prefer *Automatic (Delayed Start)* so it doesn't slow login). The same binary also runs as a **console app** for dev/foreground. **Windows-only; no UI.**
* [ ] **Configuration via file + env only** (`appsettings.json` + environment variables, standard .NET config): the loopback **listen port**, the **OTLP endpoint / protocol / headers** (per Observability), log level, etc. The developer edits the config file — there is no settings UI.
* [ ] **Graceful shutdown on the service stop signal** (Service Control Manager stop / `sc stop`, or Ctrl+C when run as a console): stop accepting connections, dispose every `RoslynInstance` (workspaces + file watchers, per Lifecycle), flush OpenTelemetry, then exit. (This replaces the old tray "Shutdown".)
* [ ] **Localhost only — not configurable.** Bind the MCP server exclusively to loopback (`127.0.0.1` / `::1`) and reject any non-local connection; no option to expose externally. Security model in place of path-allowlisting (declined): the only reachable *inbound* caller is a process on the same machine, so the server trusts its inputs. (Telemetry export is the one *outbound* connection — a client call to the configured OTLP endpoint, which may be remote like Honeycomb; not an exposed listener.) Remote inbound access would be a separate, deliberate design with its own auth — out of scope.
* [ ] **Windows-only — by design.** Running on Windows means the full MSBuild / .NET SDK surface is available, including Windows-specific TFMs (`net*-windows`, WPF/WinForms) and classic .NET Framework projects — which suits the MAUI/Blazor stack this targets. Requires the **.NET SDK** installed on the box (per §0). Still surface any project that fails to load as a clear load diagnostic, never a silent failure.
* [ ] **Distribute via WinGet.** Ship a **WiX MSI** (`win-x64`) that lays down the files and **registers the Windows Service** (`ServiceInstall` + `ServiceControl`: Automatic / Delayed start; stop + remove on uninstall) — an MSI, **not** MSIX (which can't cleanly host a classic service) and **not** a bare portable exe / `dotnet tool` (those don't register a service). **Code-sign** the MSI, attach it to a **GitHub Release**, then publish a `microsoft/winget-pkgs` **manifest** (`InstallerType: wix`, machine scope) so `winget install Morris.Roslynk` installs and starts the service. Declare the **.NET SDK** prerequisite via winget `Dependencies.PackageDependencies` (e.g. `Microsoft.DotNet.SDK.10`) and/or a clear startup check — Roslynk needs MSBuild/Roslyn at runtime. Framework-dependent publish is fine (the SDK, hence the runtime, is a prerequisite anyway). Automate manifest updates per release with `wingetcreate` / the **WinGet Releaser** GitHub Action. A `dotnet tool` console build may also be offered, but WinGet + MSI is the service-install path. **Not** a `.dxt` / Desktop-Extension package.
