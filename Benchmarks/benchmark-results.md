# Benchmark results

Results are produced by following `Benchmarks/run.md`; each run replaces the row for its use case. Every delta is **Roslynk minus Standard**, written with an explicit sign (`+` for more, `-` for less; zero may be `0`). Time carries its unit. A negative value means Roslynk used less time or fewer tokens. Read the deltas next to the correctness result: a faster or cheaper run is not an improvement if it failed the task.

Run `20261027T165112Z-find_symbol` — one run of the `find_symbol` use case, conditions run sequentially in order `standard` then `roslynk`.

| Test | Standard time | Roslynk time | Roslynk time delta | Standard tokens in | Roslynk tokens in | Roslynk tokens delta | Standard tokens out | Roslynk tokens out | Roslynk tokens out delta | Standard total tokens | Roslynk total tokens | Roslynk total tokens delta |
| --- | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: |
| find_symbol | 38.23 s | 53.63 s | +15.40 s | 27,213 | 41,353 | +14,140 | 758 | 1,312 | +554 | 27,971 | 42,665 | +14,694 |

Both conditions passed every criterion. Per-criterion scoring, the tool-call audit, the setup calls and the run observations are in `Benchmarks/use-cases/find_symbol/benchmark-audit.md`.

## Provenance

| Field | Value |
| --- | --- |
| Harness | Hermes Agent (delegate_task subagent session) — desktop build, model DeepSeek-V4.1-Flash-1017 via custom provider |
| Model | DeepSeek-V4.1-Flash-1017 (identical in both conditions) |
| Model settings | reasoning disabled; max iterations 250; max output tokens unset (provider default); temperature unavailable (not recorded by the harness); custom provider `http://sparky1.local:8888/v1`, chat_completions |
| Starting commit | `6e78204cdd99d0035921cd03c6035278508eae94` |
| Session mechanism | Hermes delegate_task subagent: fresh isolated session, parent passes goal + context as the single user turn, harness system prompt; no follow-up prompts |
| Session records | C:/Users/X/AppData/Local/hermes/state.db (tables sessions/messages; subagent transcripts C:/Users/X/AppData/Local/hermes/cache/delegation/live/deleg_3b191156/task-0.log and deleg_2c058835/task-0.log) — standard session `20261027_165128_05befb`, roslynk session `20261027_165249_591f06` |
| Run records | not kept: the run's temporary records are deleted once this file and the audit are written |
| Tool-discovery line | `Check which tools are available to you before you start.` — sent verbatim as the last line of the user prompt in both conditions |
| Prompt hash (sha256) | `d6ee2a9be631c4566a077292d97932f951f143aef067e7adfe91fd409b9aaed3` — sha256 of `prompt.md` plus that line, identical in both conditions |
| System prompt hash (sha256) | `2793ec9167449f76ba1c5f17b9d8bf9461e1dcb593d27176650a7403a8d0ae00` (identical in both conditions) |
| Roslynk availability | harness-wide config: disabled for `standard`, enabled for `roslynk`; daemon already running on loopback (pid 32736, 0.0.1-local+913dabcc), solution opened and `Ready` before timing |

## Observations

- Both conditions produced the fully correct answer, so the deltas are a valid comparison.
- The tool-discovery line made the Roslynk condition exercise Roslynk: that session called `tool_search` twice and `tool_describe` once, then `open_solution`, `get_members`, `get_symbol_body` (twice, the first returning `error=Ambiguous` for the bare name) and `find_references`. The standard session, with Roslynk disabled, found nothing to discover and used `terminal` and `search_files` only.
- Roslynk used **more** wall clock (+15.40 s) and **more** tokens (+14,694 total) here, and still passed. Most of the difference is fixed overhead rather than the task: two `tool_search` calls (5.6 s, 12.4 KB) and a `tool_describe` (11.8 KB) that a session already holding the tools would not need.
- This run replaces the earlier `20261027T163823Z-find_symbol` run, in which the Roslynk session called no Roslynk tool at all. The two are not comparable: the earlier run sent `prompt.md` alone, this one sends `prompt.md` plus the tool-discovery line.
