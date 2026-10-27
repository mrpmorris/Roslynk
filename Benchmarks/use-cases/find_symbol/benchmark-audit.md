# find_symbol audit

## Per criterion scoring

Criteria are defined in scoring.md

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

## Tool call audit

### Standard tools

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| search_files | 2429 ms | ok | 1992 bytes |
| search_files | 2437 ms | ok | 376 bytes |
| read_file | 1174 ms | ok | 1772 bytes |
| read_file | 1183 ms | ok | 425 bytes |
| read_file | 1191 ms | ok | 1223 bytes |
| read_file | 1195 ms | ok | 1008 bytes |
| read_file | 1200 ms | ok | 948 bytes |
| read_file | 1205 ms | ok | 616 bytes |
| search_files | 1031 ms | ok | 1980 bytes |
| search_files | 1055 ms | ok | 2525 bytes |
| read_file | 1915 ms | ok | 711 bytes |
| read_file | 1924 ms | ok | 619 bytes |
| read_file | 1943 ms | ok | 656 bytes |
| search_files | 1948 ms | ok | 2236 bytes |

### Roslynk

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 468 ms | ok | 119 bytes |
| tool_describe | 570 ms | ok | 6275 bytes |
| mcp__roslynk__open_solution | 35 ms | ok | 693 bytes |
| mcp__roslynk__search_symbols | 97 ms | ok | 788 bytes |
| read_file | 628 ms | ok | 1772 bytes |
| read_file | 634 ms | ok | 425 bytes |
| mcp__roslynk__get_callers | 74 ms | ok | 907 bytes |
| terminal | 468 ms | ok | 1561 bytes |
| read_file | 838 ms | ok | 1223 bytes |
| read_file | 843 ms | ok | 1008 bytes |
| read_file | 847 ms | ok | 948 bytes |

## Measurements

Every delta is **Roslynk minus Standard**, written with an explicit sign (`+` for more, `-` for less; zero may be `0`). A negative value means Roslynk used less time or fewer tokens.

| Condition | Time | Tokens in | Tokens out | Total tokens |
| --- | --: | --: | --: | --: |
| Standard | 39.66 s | 30,620 | 1,493 | 32,113 |
| Roslynk | 26.98 s | 13,278 | 1,190 | 14,468 |
| Delta (Roslynk minus Standard) | -12.68 s | -17,342 | -303 | -17,645 |

## Setup (excluded from task totals)

- find_symbol
  - Roslynk
    - `open_solution` — 2026-10-27T17:19:55Z — `C:/Data/ZZZScratch/Source/Roslynk/roslynk-bench-20261027T171838Z/RoslynkEnabled-find_symbol/Benchmarks/TestSolution/TestSolution.slnx` — projects=3, loadDiagnostics=0, 1701 ms
    - `get_solution_status` — 2026-10-27T17:19:57Z — poll 1 — Ready,3/3
    - Setup duration: 1.71 s (from `open_solution` to the first `Ready`); retries 0, failures 0
    - Tool registration: the Roslynk tools reach a delegate_task session through the harness's deferred `tool_call` registry, so they could not be registered ahead of the session; the session's own `tool_describe` (570 ms, 6275 bytes) is recorded here as setup, not as task cost.
  - Standard
    - No setup step; no tool registration was required, so `setup` is null. The session made no discovery call.

## Provenance

| Field | Value |
| --- | --- |
| Run | `20261027T171838Z-find_symbol` — conditions run sequentially, `standard` then `roslynk` |
| Harness | Hermes Agent (delegate_task subagent session) — desktop build, model DeepSeek-V4.1-Flash-1017 via custom provider |
| Model | DeepSeek-V4.1-Flash-1017 (identical in both conditions) |
| Model settings | reasoning disabled; max iterations 250; max output tokens unset (provider default); temperature unavailable (not recorded by the harness); custom provider `http://sparky1.local:8888/v1`, chat_completions |
| Starting commit | `440a9cf8be7033803edd1ec0e00d4c58fc216d89` |
| Session mechanism | Hermes delegate_task subagent: fresh isolated session, parent passes goal + context as the single user turn, harness system prompt; no follow-up prompts |
| Session records | C:/Users/X/AppData/Local/hermes/state.db (tables sessions/messages; subagent transcripts C:/Users/X/AppData/Local/hermes/cache/delegation/live/deleg_e88eca62/task-0.log and deleg_090395bb/task-0.log) — standard session `20261027_171905_fe335b`, roslynk session `20261027_172014_0b2dcc` |
| Run records | not kept: the run's temporary records are deleted once this file and the results table are written |
| Tool-discovery line | `Check which tools are available to you before you start.` — sent verbatim as the last line of the user prompt in both conditions |
| Prompt hash (sha256) | `d6ee2a9be631c4566a077292d97932f951f143aef067e7adfe91fd409b9aaed3` — sha256 of `prompt.md` plus that line. As delivered, the harness's user turn also carries the condition's workspace path, so the two delivered turns differ only in that path (`RoslynkDisabled-find_symbol` vs `RoslynkEnabled-find_symbol`); with that path normalized they hash identically to `ccb22be7a3d932e86d618320b5d9e125a44296d9514c6a89891f6fe67e62460c` |
| System prompt hash (sha256) | `3666af76e9c804a5a4af9e159d3fde40ebc396253b6edfc77aa4061cb5365e02` (identical in both conditions) |
| Roslynk availability | harness-wide config: disabled for `standard`, enabled for `roslynk`; daemon already running on loopback (pid 32736, 0.0.1-local+913dabcc), solution opened and `Ready` before timing |
| Tools registered before the session | `standard`: the harness's non-Roslynk tools; `roslynk`: the same plus the 29 `mcp__roslynk__*` tools, which the harness exposes through its deferred `tool_call` registry. Registration required no work in either condition, so `standard`'s `setup` is null |
| Tool discovery paid by the measured session | `standard`: none. `roslynk`: one `tool_describe` (570 ms, 6,275 bytes of schemas), recorded as setup and excluded from task totals |

## Observations

- Both conditions produced the fully correct answer, so the deltas are a valid comparison.
- Roslynk used **less** wall clock (-12.68 s) and **fewer** tokens (-17,342 in, -17,645 total), with 11 tool calls against 14.
- Roslynk calls were issued through the harness's generic `tool_call` wrapper; the Roslynk table lists the tool that was invoked through it.
- The Roslynk session did the semantic work with Roslynk: `search_symbols` found both `StockLedger` overloads, `get_callers` on `IStockLedger.ApplyAdjustment(string, int)` returned the three call sites with their containing members, and it read source only to confirm line numbers. It still ran one `rg` and four `read_file` calls, so it did not turn the semantic results straight into an answer.
- Its one `tool_describe` (570 ms, 6,275 bytes) is deferred-registry overhead, charged as setup. In this harness the Roslynk tools reach a delegate_task session through the deferred catalog, so `run.md` §5 step 0 could not be fully met for `roslynk`. Unlike the previous run, no `tool_search` was needed — the session asked for the tool names directly.
- The standard session never needed a discovery step: with Roslynk disabled there was nothing in its deferred catalog worth loading.
- The conditions ran sequentially: the standard session ran 17:19:06Z–17:19:45Z, Roslynk setup ran 17:19:55Z–17:19:57Z, and the Roslynk session ran 17:20:15Z–17:20:42Z. No daemon restart happened between setup and the measured session.
- Roslynk availability is harness-wide configuration: `mcp_servers.roslynk.enabled` was set to false for the standard session and restored to true before the Roslynk session (backup `config.yaml.bench-bak-20261027T171856Z`, verified by grep in both states). The standard session's tool log contains no Roslynk call.
- The standard session reached the same answer with text search alone: four `search_files` calls and ten `read_file` calls, each taking 1.0–2.4 s because they were issued in parallel batches. It passed every criterion, including correctly excluding the commented-out call and the `AuditLedger` decoy.
- Setup was issued by the orchestrating session directly against the daemon's MCP endpoint (`POST http://localhost:6502/`; the daemon maps MCP at the site root, so `/mcp` returns 404) because the orchestrating session's deferred-tool registry no longer exposed the Roslynk tools. The solution reached `Ready,3/3` on the first poll in 1.71 s.
- The subagent's recorded cwd is the parent checkout (`C:/Data/ZZZScratch/Source/Roslynk/174`), not the condition worktree; both sessions addressed the fixture by absolute path inside their own worktree, and neither read anything outside `Benchmarks/TestSolution`. Both worktrees were clean before and after.
- Per-tool-call token usage is not recorded by the harness: it reports usage per session and per model call, so the audit lists each tool's time and result size but no per-call tokens. Temperature and provider-side thinking budgets are likewise not recorded.
- Neither session hit the 10-minute or 40-turn limits; both ended on their own with `end_reason=agent_close`.
- The run's temporary records were deleted once this audit and the results row were written; nothing from the run is kept except those two files.
- The measured task is unchanged from `prompt.md`; the only addition is the generic tool-discovery line mandated by `run.md` §2. The harness also prefixes the user turn with the session goal, which carries the condition's workspace path, so the delivered turns differ only in that path; with it normalized both prompts hash identically. Because the starting commit differs from the previous run, this run is not comparable with it.
