# Running the Roslynk benchmarks

Audience: an AI agent asked to run the Roslynk benchmarks. Follow this file exactly. Read `Benchmarks/README.md` for what the suite is for, then the use cases under `Benchmarks/use-cases/` for the measured tasks.

Do not write a harness adapter, a driver script, or any program that launches the harness. You are operating the harness, not building a tool around it: start a session, let it do the task, and read what the harness recorded. The measured run is the harness's own session.

## 0. Use cases

A use case is one directory directly under `Benchmarks/use-cases/`, named for the task it measures. Its name is written `{$useCase}` below. This file names no use case: discover them at run time.

- List the directories directly under `Benchmarks/use-cases/`, sorted by name. Record that list as the run's use cases.
- A directory is a use case if it holds `prompt.md` and `scoring.md`. It may also hold an optional `setup.md` (section 11). Skip a directory that lacks either required file, and record that you skipped it and why.
- Run every use case in the same pass, one after another in that order. Each use case is run twice, standard then Roslynk (sections 4 and 6), and a use case's standard session must finish before its Roslynk session starts. Both conditions run in the repository's current checkout and `Benchmarks/TestSolution` is reset recursively before every session (section 3).
- `{$useCase}` substitutes the directory name everywhere it appears: paths, record fields and the results table's `Test` column.

## 1. Identify the harness

Before starting anything, determine and record:

- Harness name and version.
- The mechanism it offers for a fresh session with a fixed system prompt and one user prompt (a subagent/task, a new chat, a CLI prompt, an API call).
- Model and model settings (temperature, reasoning/thinking settings, max output tokens).
- What the harness records for a session: input/output/total tokens, per-turn or per-tool-call usage, tool-call names and durations, timestamps, and where that record lives.
- Whether the harness can expose the Roslynk MCP tools to a session at all, and how. Per-session toggling is not required: availability is enforced by the prompt (section 2), not by harness settings, so a harness that only reads its configuration at startup is fine.

When recording where a session's measurements live, distinguish the session store (which persists token usage and per-call records) from the launch mechanism's return value (which may carry only a summary). Read measurements from the store.

If the harness cannot start a fresh session with a fixed prompt, or does not record a measurement this file asks for, record that limitation in the run record and in `Benchmarks/benchmark-results.md`. Never invent a value.

## 2. Prepare the sessions

Both conditions use the same session setup; only the final line of the user prompt differs.

- Start one fresh session per condition, through the mechanism recorded in section 1.
- Working directory: the repository root, on its current branch — the same checkout for both conditions (section 3).
- System prompt: the harness's normal system prompt, identical in both conditions.
- User prompt: the entire contents of `Benchmarks/use-cases/{$useCase}/prompt.md`, byte for byte, followed by the tool-discovery line below, then that condition's final line below, and nothing else.
- Tool-discovery line: `Check which tools are available to you before you start.` Send it verbatim in both conditions, immediately after `prompt.md`'s last line: it is the last line of the Roslynk condition's prompt and the second-to-last of the standard condition's. It is generic — it names no tool and no product — so it does not tell the session which tools are expected to help; it only makes deferred or on-demand tool registries discoverable in both conditions alike.
- Standard prohibition line: `Do not use Roslynk`. Send it verbatim, as the last line of the standard condition's user prompt, after the tool-discovery line. Do not send it in the Roslynk condition. Do not reword, soften or extend it, and do not mention Roslynk anywhere else in either prompt.
- Roslynk preference line: `Prefer use of Roslynk tools where appropriate`. Send it verbatim, as the last line of the Roslynk condition's user prompt, after the tool-discovery line. Do not send it in the standard condition. Do not reword, soften or extend it, and do not mention Roslynk anywhere else in either prompt.
- Model, model settings, stopping rules, time limit and output requirements: identical in both conditions.
- Do not tell the session that it is being benchmarked, and do not tell it which tools are expected to help. The two condition lines are the only exceptions: the standard prohibition line names Roslynk only to bar its use, and the Roslynk preference line names it only to steer its use.
- Roslynk availability is enforced by those condition lines, not by harness configuration. Do not change the harness's own MCP configuration between the conditions: both sessions get the same tool list, so a harness that only reads its settings at startup does not need a restart. Confirm and record each session's tool list, and confirm that the standard session used no Roslynk tool (section 7). That list must be complete when the session starts (section 5, step 0), not assembled by the session during the task.
- Run the two conditions sequentially, never in parallel: the standard session must finish before the Roslynk session starts.

Every use case is two required files plus an optional third: `prompt.md` contains only the prompt, so it is sent in full, followed by the tool-discovery line, and there is nothing to strip; `scoring.md` contains the expected answer and scoring criteria and is used only in section 7; `setup.md` is optional and covered in section 11. Each condition's final line — the standard prohibition line or the Roslynk preference line — is added by the runner, never written into `prompt.md`.

Measurements come from the harness's own session record: wall clock around the session, token usage, and the tool-call log with durations where it exposes them. Do not instrument tool calls yourself, and do not turn the session's output into numbers by hand. If the harness does not expose a value, record it as `unavailable` and name it in `limitations`.

**Token capture.** Read each session's input, output and total token counts from the harness's own persistent session record — the store the harness writes after a session ends — not from the summary its task-launch mechanism returns. A launch summary that omits usage is not evidence that usage is unavailable. Record `apiCallCount` and any separately reported cache-read or reasoning tokens alongside, and state which counts are included in `inputTokens`. If the harness's session record does not expose a value, record `null` and name it in `limitations`.

## 3. Reset the test solution

No worktrees. Every session runs in the repository's current checkout, on its current branch, working in `Benchmarks/TestSolution`. That directory is reset recursively before every session, so each condition starts from exactly the same `TestSolution` content.

The order is fixed:

1. At the very start of everything — before any use case's session — record `startingCommit` and reset `Benchmarks/TestSolution` ("Reset" below). Do this once.
2. Loop through the use cases, in the order recorded in section 0.
3. For each use case:
	1. Reset `Benchmarks/TestSolution` for the standard condition ("Reset" below).
	2. Run the standard session (section 4).
	3. Reset `Benchmarks/TestSolution` again ("Reset" below).
	4. Apply that use case's `setup.md` (section 11) if it has one, then the Roslynk setup (section 5).
	5. Run the Roslynk session (section 6).
	6. Reset `Benchmarks/TestSolution` again, so the next condition or use case starts clean.

**Reset.** From the repository root, restore every file under `Benchmarks/TestSolution` to its committed content, remove everything untracked inside it, and clear its build output:

```bash
git rev-parse HEAD                     # record this as startingCommit
git checkout -- Benchmarks/TestSolution
git clean -fdx Benchmarks/TestSolution
Benchmarks/TestSolution/scripts/reset.sh
```

This is a hard reset of that one folder and nothing else. The pathspec limits all three commands to `Benchmarks/TestSolution`: no other path in the checkout is touched, the working tree's branch is not changed, and `git reset --hard` (which would reset the whole checkout) is never used. `git clean -fdx` with that pathspec deletes untracked and ignored files — including `bin`/`obj` and any file a session created — inside `Benchmarks/TestSolution` only.

Verify the reset and record the output of each check:

- `git status --porcelain -- Benchmarks/TestSolution` is empty.
- `git rev-parse HEAD` still equals `startingCommit`.
- `Benchmarks/TestSolution` contains no `bin` or `obj` directory.
- `Benchmarks/TestSolution/scripts/reset.sh` has been run, so `TestSolution` starts clean.

Do not reset or clean any path outside `Benchmarks/TestSolution`; if the checkout has other uncommitted work, leave it alone.

## 4. Run the standard condition

Start one session barred from Roslynk, working in the repository root. This condition has no setup step, so its measured duration is the whole session.

- Reset `Benchmarks/TestSolution` before starting (section 3, use-case step 1).
- Its user prompt ends with the standard prohibition line `Do not use Roslynk`, after the tool-discovery line.
- The Roslynk tools are still in the session's tool list — availability is identical to the Roslynk condition; only the instruction differs. Do not remove them from the harness.
- Time limit: 10 minutes of wall clock.
- Turn limit: 40 assistant turns, if the harness supports a turn cap.
- No hints, no follow-up prompts, no retries of the task. If the harness itself errors before the task starts, start one replacement session and keep both records; if it errors during the task, record the failure.
- A session that hits the time or turn limit is recorded as `status: "timeout"` and its partial answer is kept.
- The session must not be told that it is being benchmarked, and must not be told which tools are expected to help.

Do not start that use case's Roslynk session until this one has finished.

When this session has finished and its record is saved, reset `Benchmarks/TestSolution` again (section 3, use-case step 3), so the Roslynk condition starts from identical content.

## 5. Roslynk setup (not measured)

Immediately before the Roslynk session, in the repository root — after `Benchmarks/TestSolution` has been reset again at use-case step 3 of section 3. You perform this setup yourself; it does not belong in the measured session.

0. **Register the tools before timing, in both conditions.** A tool registry that hands the session its tools lazily or on demand makes the session pay a discovery cost — searching for tools, then loading their schemas — that it pays once, not on every use of the tools. That cost is harness overhead, not part of the task, so it must not land in the measured session. Make the session's tool list explicit before the measured session starts: the condition's tools are registered and visible at the start of the session, with no discovery step required. Both sessions get the same list, Roslynk tools included; only the standard condition's prompt forbids using them. Record the tool list you registered, and record any tool-discovery calls the session still makes (with their durations and result sizes) as setup, not as task cost. If the harness cannot register tools ahead of the session, record that limitation and treat any discovery calls the session makes as harness overhead in `limitations`, not as Roslynk's cost.
1. Make sure the Roslynk daemon is running and exposed to the harness.
2. Call `open_solution` with the absolute path of `Benchmarks/TestSolution/TestSolution.slnx` in the repository root, and record the echoed `solutionId`.
3. Poll `get_solution_status` with that `solutionId` until its line reports `Ready`. Treat `Faulted` as a setup failure; record it and do not start the measured session. Record every poll and every retry.
4. Record setup duration (from the `open_solution` call to the first `Ready` observation), the tokens those calls consumed, retries and failures.

Keep these figures in a separate `setup` object. Do not add them to task totals. The `standard` condition has no Roslynk setup, but it does have the tool registration in step 0: record that in its `setup` object too, or `"setup": null` when registration required no work.

Start measured timing only once Roslynk reports `Ready`, and do not leave a gap that lets the daemon evict the solution.

If the harness restarts the daemon between setup and the measured session, the solution reloads inside the measured session and inflates Roslynk time: record that as a setup failure and note it in the results.

## 6. Run the Roslynk condition

Reset `Benchmarks/TestSolution` before starting (section 3, use-case step 3), then complete the setup in section 5. Start one session with Roslynk available, working in the repository root, under the same limits and rules as section 4. Its user prompt ends with the Roslynk preference line `Prefer use of Roslynk tools where appropriate`, after the tool-discovery line, and carries no prohibition line; that one line is the only difference between the two conditions' prompts.

Always run the conditions in the same order (`standard`, then `roslynk`) and record that order, so separate benchmark runs stay comparable. When the Roslynk session has finished and its record is saved, reset `Benchmarks/TestSolution` again (section 3, use-case step 6). Then move to the next use case, in the order recorded in section 0, repeating the loop in section 3.

## 7. Score the answer

Score each condition's final answer against that use case's `Benchmarks/use-cases/{$useCase}/scoring.md`, criterion by criterion, exactly as that file defines them. Record each criterion as pass or fail, plus any false positives or omissions, in `Benchmarks/use-cases/{$useCase}/benchmark-audit.md`. Do not edit the answer to make it pass, and do not add criteria of your own.

Before scoring, check both sessions' tool logs. A standard session that called a Roslynk tool broke its prohibition line: record it as invalid, score it as it ran, and say so in the reply rather than presenting its numbers as a comparison. A Roslynk session that called no Roslynk tool measured nothing: record that too, and do not attribute its deltas to Roslynk.

## 8. Run record format

Write one JSON file per condition per use case, e.g. `{$runDir}/{$useCase}-standard.json` and `{$runDir}/{$useCase}-roslynk.json`, in a temporary directory outside the repository (for example `{$tempRoot}/roslynk-bench-{$runId}/`), with the task prompt and the session record copied into the same directory. These are working files for producing the results table and the audits; they are not kept after the run. Fields:

```json
{
  "runId": "{$utcTimestamp}-{$useCase}",
  "condition": "standard | roslynk",
  "useCase": "{$useCase}",
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
  "prohibitionLine": "",
  "preferenceLine": "",
  "startingCommit": "",
  "workingDirectory": "",
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

`useCase` is the directory name under `Benchmarks/use-cases/`. `workingDirectory` is the repository root both conditions ran in, and `startingCommit` the commit `Benchmarks/TestSolution` was reset to before each session. `roslynkAvailable` records whether the Roslynk tools were in the session's tool list — true in both conditions under this procedure. `prohibitionLine` holds `Do not use Roslynk` in `standard` and is empty in `roslynk`; `preferenceLine` holds `Prefer use of Roslynk tools where appropriate` in `roslynk` and is empty in `standard`. So `promptSha256` is per condition and is not shared between them.

Take per-tool-call tokens from the assistant turn that issued the call when the harness records them that way; if it reports usage only per session, set the per-call token fields to `null`.

## 9. Update the results files

Regenerate the results and the audits from the saved records; do not hand-edit numbers.

`Benchmarks/benchmark-results.md` contains only:

- The title, `# Benchmark results`.
- `Last run on {$yyyy-MM-dd HH:mm:ss}` — the run's start time.
- The table: one row per use case, with `Test` (the use case's directory name), `Standard pass %`, `Roslynk pass %`, then the time and token columns — Standard time, Roslynk time, Roslynk time delta, Standard tokens in, Roslynk tokens in, Roslynk tokens delta, Standard tokens out, Roslynk tokens out, Roslynk tokens out delta, Standard total tokens, Roslynk total tokens, Roslynk total tokens delta.

A pass % is the share of the criteria in that use case's `scoring.md` that the condition's answer passed (`8/8` is `100%`). Every delta is **Roslynk minus Standard**, written with an explicit sign (`+` or `-`; zero may be `0`), and time carries its unit (e.g. `+12.3 s`). Nothing else goes in that file.

`Benchmarks/use-cases/{$useCase}/benchmark-audit.md` contains, for the most recent run of that use case:

- `## Per criterion scoring`: the line `Criteria are defined in scoring.md`, then the criterion table with a pass or fail for each condition.
- `## Tool call audit`: two tables, `### Standard tools` and `### Roslynk`, each with the columns `Tool name | Time | Outcome | Result size` and one row per tool call in call order. Where a Roslynk call is recorded through the harness's generic `tool_call` wrapper, list the invoked tool name. Omit a field the harness did not expose rather than writing `unavailable` in a cell.

Nothing follows the `### Roslynk` table.

Replace each audit's contents on every run; it describes only the latest run of that use case and is never appended to.

In the results table, write `unavailable` for any cell the harness did not expose. The results table and the audits are the record of the run: once all of them are written, delete the temporary run directory. Nothing from a run is kept except those files.

## 10. Clean up

`Benchmarks/TestSolution` is reset inside the use-case loop: right after each session (section 3, use-case steps 3 and 6). At the end of the run, reset it once more and verify:

```bash
git checkout -- Benchmarks/TestSolution
git clean -fdx Benchmarks/TestSolution
Benchmarks/TestSolution/scripts/reset.sh
git status --porcelain -- Benchmarks/TestSolution
```

`git status --porcelain -- Benchmarks/TestSolution` must be empty, and `git rev-parse HEAD` must still equal `startingCommit`.

Then delete the temporary run directory (`rm -rf "$runDir"`) once the results table and the audits have been written from it, and verify that no benchmark directories remain.

Scope every reset and clean to `Benchmarks/TestSolution` and the temporary run directory. Never reset or clean any other path in the checkout.

## 11. Use-case setup files

A use case may hold an optional `setup.md` alongside `prompt.md` and `scoring.md`. If it does, apply it identically in both conditions, before that condition's session starts, and record its time and tokens separately from task totals. It must not use Roslynk. A use case with no `setup.md` needs no setup step.
