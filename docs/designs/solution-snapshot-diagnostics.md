# Design: Snapshot-Scoped On-Demand Diagnostics

**Author:** Peter Morris
**Status:** Implemented on branch `202/PM-DiagnosticsCacheInSnapshots` (this revision)
**Source issue:** #202 — Bind Roslynk diagnostics to solution snapshots

## The Problem

A reader can hold an immutable Roslyn solution snapshot while an edit publishes a newer one. The
queued diagnostics path read the *current* solution when it ran, so a reader holding snapshot A
could receive diagnostics computed for snapshot B. Future plugins (#199) need diagnostics for the
exact snapshot they hold.

## Goals

- A diagnostics request for snapshot A returns results for A — including its Razor diagnostics —
  even after B is published.
- Concurrent and repeat requests for the same snapshot share one computation; a failed or
  cancelled computation can be retried.
- Loading or publishing a snapshot leaves diagnostics available on demand.

## Non-Goals

- Telemetry/observability for the pipeline (separate change).
- Snapshot echo/pinning across paging tools (tracked as issue #203).

## Final Shape: SolutionSnapshot (this revision, Peter's decisions)

**Decisions (Peter):**

1. Delete the weak table. The cache is one **`SolutionSnapshot`** instance per publication.
2. `DiagnosticsService` stops memoizing and stops differentiating compiler vs analyzer: it
   computes **all** diagnostics (always with analyzers) for one project, statelessly. The
   **consumer decides** which diagnostics to include in its response.

### SolutionSnapshot

Private nested class in `RoslynInstance`:

- `Solution Solution` — the snapshot it was built from.
- `Dictionary<Project, Task<ImmutableArray<Diagnostic>>> Tasks` — keyed with
  `ReferenceEqualityComparer.Instance` (Project uses reference identity; explicit beats implicit).
- One `Lock Gate`. `GetOrAddAsync(project, compute, …)`: an existing reusable task (in-flight or
  completed successfully) is returned; a faulted/cancelled task is replaced by a fresh
  `TaskCompletionSource` (`RunContinuationsAsynchronously`) whose compute is enqueued on the
  channel (ordered behind pending writes) and returned.
- **Built at publication**: for each project of the new solution, carry forward the old entry when
  the `Project` instance is identical and its task is reusable. Roslyn carries unaltered projects
  by reference across forks and forks a project whenever any compilation-relevant input changes
  (own documents, options, references, or a dependency's semantic state) — instance identity *is*
  the fingerprint, so no fingerprint code is needed. A rebuild creates a new workspace, hence all
  new project instances: nothing carries (full invalidation — correct). A faulted publication
  carries no solution and leaves the previous holder untouched.
- In-flight tasks carry over: a task keyed to project instance P is valid in any snapshot
  holding P. Superseded-snapshot requests (plugins only; built-in tools always use current)
  resolve through the current holder's per-project map for free.

### DiagnosticsService (stateless)

- One public compute: `GetProjectDiagnosticsAsync(Project, CancellationToken)` —
  `GetCompilationAsync` + all analyzers in a single pass.
- `GetAllDiagnosticsAsync(Solution, CancellationToken)` = per-project computes concatenated in
  solution order (`Parallel.ForEachAsync`, `MaxDegreeOfParallelism = 4`, as today).
- Deleted: `SolutionMemo`, `Memo`, `Entry`, `Inputs`/`Matches` fingerprints, the mode axis.

### Instance API

- `RequestDiagnosticsAsync(Solution snapshot, CancellationToken)` → `DiagnosticsResult`:
  per-project tasks joined and concatenated in solution order, with the Razor capture attached.
  The compute always includes analyzers; ordering behind pending writes is preserved (channel).
- **Razor capture moves into `SolutionSnapshot`**, taken at publication before the swap — this
  also removes the swap-then-capture race the global `RazorCaptureField` had on the rebuild path.
- Requester cancellation surfaces at the `WaitAsync` await; shared tasks run on
  `CancellationToken.None` so one caller giving up never cancels a joined computation.
- `BuildNeeded` (test-only) becomes: no holder, holder solution mismatch, or no reusable tasks.

### Consumers

- **`GetDiagnosticsTool`**: `includeAnalyzers` becomes a *response filter* — `false` excludes
  non-compiler-tagged diagnostics (by `Descriptor.CustomTags`) from counts and body. The
  computation always includes analyzers, so `false` no longer means "faster"; it means "don't
  show analyzer rows". Contract text updated in the tool description, README and releases.md.
- **`DocumentDiagnosticsProvider`**: computes its compiler-only subset inline from the
  compilation it already obtains (`GetCompilationAsync` memoizes per project instance, so no
  duplicated compilation); its narrow fixable-analyzer pass is unchanged.
  `DiagnosticsService.GetCompilerDiagnosticsAsync` is removed.
- Code-action callers are unchanged (`GetForDocumentAsync` contract identical).

### What dies

`ConditionalWeakTable<Solution, DiagnosticsEntry>`, `DiagnosticsEntry`, `SolutionMemo`, `Memo`,
`Entry`, `Inputs`/`Matches`, `RazorCaptureField`, the compute-callback parameter on
`RequestDiagnosticsAsync`.

## Verification

- `RoslynInstanceTests` — pinning across publications, per-project sharing, write ordering,
  cancelled-requester semantics, retry after fault, no-op-write reuse, copy-forward reuse.
- Full engine suite + McpTests green.
- Same-session A/B of unit-test wall time, branch vs master, median of paired runs.

## A/B Benchmark (same-session, paired)

Diagnostics filter (`FullyQualifiedName~Diagnostics`), `--no-build`, median of 5, one warmup discarded:

| Side | Runs (s) | Median |
|---|---|---|
| master | 65, 70, 66, 64, 64 | 65 s |
| branch | 67, 67, 65, 68, 70 | 67 s |

+2.0 s (+3.1%) — within the run-to-run noise band (master itself spans 64–70 s). Note the two sides
run different test counts (80 vs 75: five tests were rewritten, not dropped), so per-test time is
not comparable here; the honest read is "no measurable regression on the diagnostics path".

## Implementation Notes (post-coding)

- `RequestDiagnosticsAsync(Solution, CancellationToken)` — no compute callback, no mode parameter.
  The current snapshot resolves every project through the holder; a superseded snapshot reuses only
  carried tasks and computes the rest uncached (result still exact for that snapshot).
- The Razor capture lives on the holder, taken in `SwapWithSnapshot` before the model swap — the
  rebuild path's old swap-then-capture race is gone with it.
- Tests probe the holder's `Tasks` map (project instance → task identity) via reflection
  (`SolutionSnapshotProbe`) to assert carry-forward and replacement.
- Verified: RoslynInstanceTests 23/23, DiagnosticsServiceTests 5/5, DocumentDiagnosticsProviderTests
  15/15, full engine suite, McpTests 92/92.

*Drafted with EGM*
