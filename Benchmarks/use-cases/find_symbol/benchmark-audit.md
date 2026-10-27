# find_symbol audit

Per-criterion scoring, the tool-call audit and the setup calls for the most recent `find_symbol` run. Built from the run's temporary records, which are deleted once this file and the results table are written; the time and token summary lives in `Benchmarks/benchmark-results.md`. Only the latest run is kept here — a new run replaces this file, it is never appended to.

Run `20261027T165112Z-find_symbol` — conditions run sequentially, `standard` then `roslynk`.

## Per-criterion scoring

Criteria are defined in `scoring.md`. Each call site is matched by path, line and containing member; the order the sites are listed in is not a criterion.

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

One entry per tool call, in call order. Each entry is `tool — duration — outcome — result size`. Durations are the harness's own timestamps: the gap between the assistant turn that issued the call and the tool result. Calls issued in parallel share a turn start, so they overlap. Per-call tokens are absent because the harness does not attribute usage per tool call. Where the harness recorded a Roslynk call as its generic `tool_call` wrapper, the invoked tool is shown after the arrow.

- find_symbol
  - Standard
    - terminal — 668 ms — ok — result 119 bytes
    - search_files — 1585 ms — ok — result 1980 bytes
    - terminal — 617 ms — ok — result 6674 bytes
    - terminal — 1144 ms — ok — result 1499 bytes
    - terminal — 477 ms — ok — result 617 bytes
  - Roslynk
    - tool_search — 3046 ms — ok — result 4087 bytes
    - tool_search — 2552 ms — ok — result 8270 bytes
    - tool_describe — 51 ms — ok — result 11777 bytes
    - tool_call → mcp__roslynk__open_solution — 36 ms — ok — result 695 bytes
    - tool_call → mcp__roslynk__get_members — 92 ms — ok — result 598 bytes
    - tool_call → mcp__roslynk__get_symbol_body — 54 ms — ok — result 701 bytes
    - tool_call → mcp__roslynk__get_symbol_body — 54 ms — ok — result 577 bytes
    - tool_call → mcp__roslynk__find_references — 103 ms — ok — result 912 bytes
    - execute_code — 2240 ms — ok — result 4958 bytes
    - execute_code — 554 ms — ok — result 3023 bytes

## Setup (excluded from task totals)

- find_symbol
  - Roslynk
    - `open_solution` — 2026-10-27T16:52:40Z — `C:/Data/ZZZScratch/Source/Roslynk/roslynk-bench-20261027T165112Z/RoslynkEnabled-find_symbol/Benchmarks/TestSolution/TestSolution.slnx` — projects=3, loadDiagnostics=0
    - `get_solution_status` — 2026-10-27T16:52:42Z — poll 1 — Ready,3/3
    - Setup duration: 1.7 s (from `open_solution` to the first `Ready`); retries 0, failures 0
  - Standard
    - No setup step.

## Observations

- The conditions ran sequentially: the standard session ended at 16:52:07Z, Roslynk setup ran 16:52:40Z–16:52:42Z, and the Roslynk session ran 16:52:50Z–16:53:44Z. No daemon restart happened between setup and the measured session.
- Roslynk availability is harness-wide configuration: `mcp_servers.roslynk.enabled` was set to false for the standard session and restored to true before the Roslynk session (backup `config.yaml.bench-bak-20261027T165119Z`). The standard session's tool log contains no Roslynk call.
- **The tool-discovery line did what it was added for.** In the previous run (`20261027T163823Z`) the Roslynk session called no Roslynk tool; here it opened with `tool_search` and then used five Roslynk calls. The line names no tool or product, so the difference between the conditions is still only Roslynk availability.
- Discovery cost is charged to the Roslynk condition and dominates its delta: two `tool_search` calls took 5,598 ms and returned 12,357 bytes, and `tool_describe` returned 11,777 bytes of schemas. Together that is most of the +15.40 s and +14,694 tokens. The five Roslynk calls themselves took 36–103 ms and returned 695–912 bytes each — cheaper per call than the standard session's `search_files`/`terminal` calls, but the session spent two `execute_code` calls (2,794 ms) reading files anyway, so it did not convert the semantic results straight into an answer.
- One Roslynk call was wasted on ambiguity: `get_symbol_body` with the bare name `StockLedger.ApplyAdjustment` returned `error=Ambiguous` with the two overloads as candidates, and the retry with the full parameter list succeeded. That is the tool behaving as documented, not a defect.
- Setup was issued by the orchestrating session directly against the daemon's MCP endpoint (`POST http://localhost:6502/`; the daemon maps MCP at the site root, so `/mcp` returns 404) because the orchestrating session's deferred-tool registry no longer exposed the Roslynk tools. The solution reached `Ready,3/3` on the first poll in 1.7 s.
- The subagent's recorded cwd is the parent checkout (`C:/Data/ZZZScratch/Source/Roslynk/174`), not the condition worktree; both sessions addressed the fixture by absolute path inside their own worktree, and neither read anything outside `Benchmarks/TestSolution`. Both worktrees were clean before and after.
- Per-tool-call token usage is not recorded by the harness: it reports usage per session and per model call, so the audit lists each tool's time and result size but no per-call tokens. Temperature and provider-side thinking budgets are likewise not recorded.
- Neither session hit the 10-minute or 40-turn limits; both ended on their own with `end_reason=agent_close`.
- The run's temporary records were deleted once this audit and the results row were written; nothing from the run is kept except those two files.
- The measured task is unchanged from `prompt.md`; the only addition is the generic tool-discovery line mandated by `run.md` §2. Because that line changes the setup, this run is not comparable with any earlier one, and future runs must send it too.
