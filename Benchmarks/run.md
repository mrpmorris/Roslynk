# Running the Roslynk benchmarks

Audience: an AI agent asked to run the Roslynk benchmarks. Follow this file exactly. Read `Benchmarks/README.md` for what the suite is for, `Benchmarks/use-cases/find_symbol/prompt.md` for the measured task, and `Benchmarks/use-cases/find_symbol/scoring.md` when scoring.

Do not write a harness adapter, a driver script, or any program that launches the harness. You are operating the harness, not building a tool around it: start a session, let it do the task, and read what the harness recorded. The measured run is the harness's own session.

## 1. Identify the harness

Before starting anything, determine and record:

- Harness name and version.
- The mechanism it offers for a fresh session with a fixed system prompt and one user prompt (a subagent/task, a new chat, a CLI prompt, an API call).
- Model and model settings (temperature, reasoning/thinking settings, max output tokens).
- What the harness records for a session: input/output/total tokens, per-turn or per-tool-call usage, tool-call names and durations, timestamps, and where that record lives.
- Whether one session can enable or disable the Roslynk MCP server, or whether that is harness-wide configuration you must change before the session and restore afterwards.

If the harness cannot start a fresh session with a fixed prompt, or does not record a measurement this file asks for, record that limitation in the run record and in `Benchmarks/benchmark-results.md`. Never invent a value.

## 2. Prepare the sessions

Both conditions use the same session setup; only Roslynk availability differs.

- Start one fresh session per condition, through the mechanism recorded in section 1.
- Working directory: the condition's worktree (section 3).
- System prompt: the harness's normal system prompt, identical in both conditions.
- User prompt: the entire contents of `Benchmarks/use-cases/find_symbol/prompt.md`, byte for byte, followed by the tool-discovery line below, and nothing else.
- Tool-discovery line: `Check which tools are available to you before you start.` Send it verbatim, as the last line of the user prompt, in both conditions. It is generic — it names no tool and no product — so it does not tell the session which tools are expected to help; it only makes deferred or on-demand tool registries discoverable in both conditions alike.
- Model, model settings, stopping rules, time limit and output requirements: identical in both conditions.
- Do not tell the session that it is being benchmarked, and do not tell it which tools are expected to help.
- Roslynk availability is harness configuration for that session, not prompt text. Confirm the session's tool list matches the condition — no Roslynk tool in `standard`, the Roslynk tools in `roslynk` — and record that list. That list must be complete when the session starts (section 5, step 0), not assembled by the session during the task.
- Run the two conditions sequentially, never in parallel: the standard session must finish before the Roslynk session starts.

Every use case is two files: `prompt.md` contains only the prompt, so it is sent in full, followed by the tool-discovery line, and there is nothing to strip; `scoring.md` contains the expected answer and scoring criteria and is used only in section 7.

Measurements come from the harness's own session record: wall clock around the session, token usage, and the tool-call log with durations where it exposes them. Do not instrument tool calls yourself, and do not turn the session's output into numbers by hand. If the harness does not expose a value, record it as `unavailable` and name it in `limitations`.

## 3. Create the two worktrees

From the repository root, at the commit you are benchmarking:

```bash
git rev-parse HEAD                     # record this as startingCommit
repo_root="$(git rev-parse --show-toplevel)"
parent="$(dirname "$repo_root")/roslynk-bench-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$parent"
git worktree add --detach "$parent/RoslynkEnabled-find_symbol" HEAD
git worktree add --detach "$parent/RoslynkDisabled-find_symbol" HEAD
```

Use a temporary parent directory outside the repository; the sibling location above keeps every path native to the platform. For each worktree, sparse-checkout only the test solution:

```bash
cd "$parent/RoslynkEnabled-find_symbol"
git sparse-checkout init --no-cone
git sparse-checkout set Benchmarks/TestSolution
```

Use no-cone mode. Cone mode always checks out the repository's root files, which would put `AGENTS.md` and `README.md` in the measured agent's workspace and leak benchmark instructions into the run; no-cone mode checks out only `Benchmarks/TestSolution`.

Repeat in `RoslynkDisabled-find_symbol`. Then verify, and record the output of each check:

- `git -C {$worktree} rev-parse HEAD` is identical in both worktrees and equals `startingCommit`.
- `git -C {$worktree} status --porcelain` is empty in both.
- Only `Benchmarks/TestSolution` exists besides `.git` in each worktree.
- `Benchmarks/TestSolution/scripts/reset.sh` has been run in both, so `TestSolution` starts clean.

The worktree names must be exactly `RoslynkEnabled-find_symbol` and `RoslynkDisabled-find_symbol`.

## 4. Run the standard condition

Start one session with Roslynk unavailable, working in `RoslynkDisabled-find_symbol`. This condition has no setup step, so its measured duration is the whole session.

- Time limit: 10 minutes of wall clock.
- Turn limit: 40 assistant turns, if the harness supports a turn cap.
- No hints, no follow-up prompts, no retries of the task. If the harness itself errors before the task starts, start one replacement session and keep both records; if it errors during the task, record the failure.
- A session that hits the time or turn limit is recorded as `status: "timeout"` and its partial answer is kept.
- The session must not be told that it is being benchmarked, and must not be told which tools are expected to help.

Do not start the Roslynk session until this one has finished.

## 5. Roslynk setup (not measured)

Immediately before the Roslynk session, in `RoslynkEnabled-find_symbol`. You perform this setup yourself; it does not belong in the measured session.

0. **Register the tools before timing, in both conditions.** A tool registry that hands the session its tools lazily or on demand makes the session pay a discovery cost — searching for tools, then loading their schemas — that it pays once, not on every use of the tools. That cost is harness overhead, not part of the task, so it must not land in the measured session. Make the session's tool list explicit before the measured session starts: the condition's tools are registered and visible at the start of the session, with no discovery step required. Do the same for `standard`, so both sessions begin with a complete tool list and the only difference remains Roslynk availability. Record the tool list you registered, and record any tool-discovery calls the session still makes (with their durations and result sizes) as setup, not as task cost. If the harness cannot register tools ahead of the session, record that limitation and treat any discovery calls the session makes as harness overhead in `limitations`, not as Roslynk's cost.
1. Make sure the Roslynk daemon is running and exposed to the harness.
2. Call `open_solution` with the absolute path `{$worktree}/Benchmarks/TestSolution/TestSolution.slnx`, and record the echoed `solutionId`.
3. Poll `get_solution_status` with that `solutionId` until its line reports `Ready`. Treat `Faulted` as a setup failure; record it and do not start the measured session. Record every poll and every retry.
4. Record setup duration (from the `open_solution` call to the first `Ready` observation), the tokens those calls consumed, retries and failures.

Keep these figures in a separate `setup` object. Do not add them to task totals. The `standard` condition has no Roslynk setup, but it does have the tool registration in step 0: record that in its `setup` object too, or `"setup": null` when registration required no work.

Start measured timing only once Roslynk reports `Ready`, and do not leave a gap that lets the daemon evict the solution.

If the harness restarts the daemon between setup and the measured session, the solution reloads inside the measured session and inflates Roslynk time: record that as a setup failure and note it in the results.

## 6. Run the Roslynk condition

Start one session with Roslynk available, working in `RoslynkEnabled-find_symbol`, under the same limits and rules as section 4.

Always run the conditions in the same order (`standard`, then `roslynk`) and record that order, so separate benchmark runs stay comparable.

## 7. Score the answer

Score the final answer against `Benchmarks/use-cases/find_symbol/scoring.md`: declaration path and line, signature, call-site count, and each call site's path, line and containing member. Record each criterion as pass or fail, plus any false positives or omissions, in `Benchmarks/use-cases/find_symbol/benchmark-audit.md`. Do not edit the answer to make it pass.

## 8. Run record format

Write one JSON file per run, e.g. `{$runDir}/standard.json` and `{$runDir}/roslynk.json`, in a temporary directory outside the repository (for example `{$tempRoot}/roslynk-bench-{$runId}/`), with the task prompt and the session record copied into the same directory. These are working files for producing the results table and the audit; they are not kept after the run. Fields:

```json
{
  "runId": "{$utcTimestamp}-find_symbol",
  "condition": "standard | roslynk",
  "useCase": "find_symbol",
  "startedUtc": "{$iso8601Timestamp}",
  "endedUtc": "{$iso8601Timestamp}",
  "durationSeconds": 0.0,
  "status": "completed | timeout | error",
  "harness": { "name": "", "version": "" },
  "session": { "mechanism": "", "id": "", "recordPath": "" },
  "model": "",
  "modelSettings": {},
  "systemPromptSha256": "",
  "promptSha256": "",
  "toolDiscoveryLine": "",
  "startingCommit": "",
  "worktree": "",
  "roslynkAvailable": false,
  "observedTools": [],
  "registeredTools": [],
  "usage": { "inputTokens": 0, "outputTokens": 0, "totalTokens": 0 },
  "toolCalls": [
    { "name": "", "durationMs": 0, "tokensIn": 0, "tokensOut": 0, "totalTokens": 0 }
  ],
  "finalAnswer": "",
  "filesChanged": [],
  "setup": null,
  "scoring": { "passed": false, "criteria": [], "notes": "" },
  "limitations": []
}
```

Use `null` for any value the harness does not expose, and name it in `limitations`. Do not write a plausible number in place of a missing measurement.

Take per-tool-call tokens from the assistant turn that issued the call when the harness records them that way; if it reports usage only per session, set the per-call token fields to `null`.

## 9. Update the results file

Regenerate the results and the audit from the saved records; do not hand-edit numbers.

`Benchmarks/benchmark-results.md` contains:

- One row per use case in the table specified there. Every delta is **Roslynk minus Standard**, written with an explicit sign (`+` or `-`; zero may be `0`), and time carries its unit (e.g. `+12.3 s`). A negative value means Roslynk used less time or fewer tokens.
- The provenance line: harness, model, model settings, starting commit and the session mechanism and record location.
- The tool-discovery line that was sent with the prompt, and the sha256 of the full user prompt as sent (prompt plus that line), so the setup can be reproduced exactly.
- The tool list registered before the session started (section 5, step 0), and any discovery calls the measured session still made, recorded as setup rather than as task cost.

`Benchmarks/use-cases/<use-case>/benchmark-audit.md` contains, for the most recent run of that use case:

- The per-criterion scoring table built from `scoring.md`, with a pass or fail for each condition.
- The tool-call audit grouped by condition, one entry per tool call, in call order, in the format specified there. Omit a field the harness did not expose rather than writing `unavailable` inside an entry.
- A separately labelled setup section listing `open_solution` and status-polling calls, visible but excluded from task totals.
- Your observations at the bottom of the file.

Replace the audit's contents on every run; it describes only the latest run and is never appended to.

In the results table, write `unavailable` for any cell the harness did not expose. The results table and the audit are the record of the run: once both are written, delete the temporary run directory. Nothing from a run is kept except those two files.

## 10. Clean up

Remove both temporary worktrees and verify they are gone:

```bash
git worktree remove --force "$parent/RoslynkEnabled-find_symbol"
git worktree remove --force "$parent/RoslynkDisabled-find_symbol"
git worktree prune
git worktree list
```

Then delete the temporary run directory (`rm -rf "$runDir"`) once the results table and the audit have been written from it, and verify that no benchmark directories remain.

Scope every reset and clean to the benchmark worktrees and the temporary run directory. Never reset or clean the main checkout.

## 11. Use-case setup files

`find_symbol` needs no setup file. If a future use case adds one, it must not use Roslynk, must be applied identically in both worktrees before measurement, and its time and tokens are recorded separately from task totals.
