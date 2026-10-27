# find_symbol audit

Per-criterion scoring, the tool-call audit and the setup calls for the most recent `find_symbol` run. Regenerated from the raw run records in `Benchmarks/run-records/`; the time and token summary lives in `Benchmarks/benchmark-results.md`. Only the latest run is kept here — a new run replaces this file, it is never appended to.

Run `20261027T161718Z-find_symbol` — conditions run sequentially, `standard` then `roslynk`.

## Per-criterion scoring

Criteria are defined in `scoring.md`.

| Criterion | Standard | Roslynk |
| --- | --- | --- |
| Declaration path | pass | pass |
| Declaration line | pass | pass |
| Signature | pass | pass |
| Call-site count | pass | pass |
| Call site 1 | pass | pass |
| Call site 2 | pass | pass |
| Call site 3 | pass | pass |
| No false positives | pass | pass |
| Overall | pass | pass |

## Tool-call audit

One entry per tool call, in call order. Each entry is `tool — duration — outcome — result size`. Durations are the harness's own timestamps: the gap between the assistant turn that issued the call and the tool result. Calls issued in parallel share a turn start, so they overlap. Per-call tokens are absent because the harness does not attribute usage per tool call.

- find_symbol
  - Standard
    - search_files — 1291 ms — ok — result 1650 bytes
    - search_files — 1299 ms — ok — result 1071 bytes
    - read_file — 1488 ms — ok — result 1772 bytes
    - read_file — 1498 ms — ok — result 425 bytes
    - read_file — 1523 ms — ok — result 1223 bytes
    - read_file — 1528 ms — ok — result 1008 bytes
    - read_file — 1533 ms — ok — result 948 bytes
    - read_file — 1564 ms — ok — result 616 bytes
    - read_file — 1370 ms — ok — result 711 bytes
    - read_file — 1378 ms — ok — result 619 bytes
    - read_file — 1383 ms — ok — result 1085 bytes
    - read_file — 1389 ms — ok — result 656 bytes
    - search_files — 1393 ms — ok — result 1558 bytes
    - terminal — 403 ms — ok — result 759 bytes
  - Roslynk
    - tool_search — 3203 ms — ok — result 8026 bytes
    - mcp__roslynk__search_symbols — 128 ms — ok — result 790 bytes
    - mcp__roslynk__find_definition — 54 ms — ok — result 552 bytes
    - mcp__roslynk__find_definition — 93 ms — ok — result 560 bytes
    - mcp__roslynk__find_references — 122 ms — ok — result 912 bytes
    - read_file — 620 ms — ok — result 1772 bytes
    - read_file — 834 ms — ok — result 1008 bytes
    - read_file — 838 ms — ok — result 948 bytes
    - read_file — 843 ms — ok — result 1223 bytes

## Setup (excluded from task totals)

- find_symbol
  - Roslynk
    - `open_solution` — 2026-10-27T16:20:02Z — `C:/Data/ZZZScratch/Source/Roslynk/roslynk-bench-20261027T161718Z/RoslynkEnabled-find_symbol/Benchmarks/TestSolution/TestSolution.slnx` — projects=3, loadDiagnostics=0
    - `get_solution_status` — 2026-10-27T16:20:08Z — poll 1 — Ready,3/3
    - Setup duration: 6.0 s (from `open_solution` to the first `Ready`); retries 0, failures 0
  - Standard
    - No setup step.

## Run records

`Benchmarks/run-records/20261027T161718Z-find_symbol/` — `standard.json`, `roslynk.json`, `setup-roslynk.json` and the prompt that was sent (`prompt.md`).

## Observations

- The conditions ran sequentially: the standard session finished at 16:19:02Z, Roslynk setup ran at 16:20:02Z and the Roslynk session started at 16:20:30Z.
- Roslynk availability is harness-wide configuration: `mcp_servers.roslynk.enabled` was set to false for the standard session and restored to true before the Roslynk session (backup `config.yaml.bench-bak-20261027T161735Z`), and the harness's tool registry was rebuilt for each session.
- The subagent's recorded cwd is the parent checkout rather than the condition worktree: the standard session resolved the fixture from the parent checkout's `Benchmarks/TestSolution` (identical content at the same commit) and the Roslynk session used absolute worktree paths. Neither session read outside `Benchmarks/TestSolution`.
- The Roslynk session reached the tools through the harness's generic `tool_call` wrapper, so the session record names those calls `tool_call`; the names in this audit were mapped from each invocation. It spent one `tool_search` call (8026 result bytes) discovering the tool names.
- Roslynk tool-call durations (54–128 ms) are harness timestamps — the gap between the issuing turn and the tool result — not Roslynk-internal timings.
- Per-tool-call token usage is not recorded by the harness: it reports usage per session and per model call, so the audit lists each tool's time and result size but no per-call tokens.
- Temperature and any provider-side thinking budget are not recorded by the harness; only its own settings are known (reasoning disabled, max iterations 250, max output tokens unset).
- Token counts are the harness's session totals for each measured session, taken from its session record.
- Neither session hit the 10-minute or 40-turn limits; both ended on their own, and both worktrees were clean at start and end.
