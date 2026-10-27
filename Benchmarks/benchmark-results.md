# Benchmark results

Results are regenerated from the raw run records in `Benchmarks/run-records/` by following `Benchmarks/run.md`. Every delta is **Roslynk minus Standard**, written with an explicit sign (`+` for more, `-` for less; zero may be `0`). Time carries its unit. A negative value means Roslynk used less time or fewer tokens. Read the deltas next to the correctness result: a faster or cheaper run is not an improvement if it failed the task.

Run `20261027T161718Z-find_symbol` — one run of the `find_symbol` use case, conditions run sequentially in order `standard` then `roslynk`.

| Test | Standard time | Roslynk time | Roslynk time delta | Standard tokens in | Roslynk tokens in | Roslynk tokens delta | Standard tokens out | Roslynk tokens out | Roslynk tokens out delta | Standard total tokens | Roslynk total tokens | Roslynk total tokens delta |
| --- | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: |
| find_symbol | 55.14 s | 46.17 s | -8.97 s | 29,688 | 31,942 | +2,254 | 1,249 | 1,013 | -236 | 30,937 | 32,955 | +2,018 |

Both conditions passed every criterion. Per-criterion scoring, the tool-call audit, the setup calls and the run observations are in `Benchmarks/use-cases/find_symbol/benchmark-audit.md`.

## Provenance

| Field | Value |
| --- | --- |
| Harness | Hermes Agent (delegate_task subagent session) — desktop build, model DeepSeek-V4.1-Flash-1017 via custom provider |
| Model | DeepSeek-V4.1-Flash-1017 (identical in both conditions) |
| Model settings | reasoning disabled; max iterations 250; max output tokens unset (provider default); temperature unavailable (not recorded by the harness); custom provider `http://sparky1.local:8888/v1`, chat_completions |
| Starting commit | `d35c9293d1646c04bd9879108724128cae2ee0b0` |
| Session mechanism | Hermes delegate_task subagent: fresh isolated session, parent passes goal + context as the single user turn, harness system prompt; no follow-up prompts |
| Session records | C:/Users/X/AppData/Local/hermes/state.db (tables sessions/messages; subagent transcripts C:/Users/X/AppData/Local/hermes/cache/delegation/live/deleg_886ff5e0/task-0.log and deleg_164fb78e/task-0.log) — standard session `20261027_161806_897c51`, roslynk session `20261027_162028_cb7013` |
| Run records | `Benchmarks/run-records/20261027T161718Z-find_symbol/` (`standard.json`, `roslynk.json`, `setup-roslynk.json`, `prompt.md`) |
| Prompt hash (sha256) | `2a6a7dae0e618d712ce69a13f464cdd15ecadd284d9ffe5c497d7bdfdd8aaafb` (identical in both conditions) |
| System prompt hash (sha256) | `2793ec9167449f76ba1c5f17b9d8bf9461e1dcb593d27176650a7403a8d0ae00` (identical in both conditions) |
| Roslynk availability | harness-wide config: disabled for `standard`, enabled for `roslynk`; daemon already running on loopback, solution opened and `Ready` before timing |
