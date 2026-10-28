# Roslynk benchmarks

This folder holds a reproducible benchmark suite that measures how Roslynk changes the cost and time an AI agent needs to complete realistic C# tasks, compared with the same agent using only its standard tools.

Roslynk's claim is that an agent can answer semantic questions about a compiled solution faster and with fewer tokens than by searching text and reading files by hand. This suite measures that claim under controlled conditions instead of by impression.

## The two conditions

Every use case is run twice, with the same model, system prompt, task prompt, session setup, stopping rules, time limit and output requirements:

1. **Standard** — the agent is told `Do not use Roslynk` as the last line of its prompt. The Roslynk tools are in its tool list, so the two runs start from the same harness state; the prohibition, not a settings change, is what bars them. The agent uses whatever else its harness provides (file search, shell commands, file reads).
2. **Roslynk** — Roslynk tools are available and the prompt ends with `Prefer use of Roslynk tools where appropriate` instead of a prohibition. The solution is opened and reports ready before measured task timing starts; that setup cost is recorded separately and excluded from task totals.

The only intended difference between the two runs is that final prompt line. A run that changes anything else is not a valid comparison.

## Layout

| Path | Purpose |
| --- | --- |
| `Benchmarks/README.md` | This file: what the suite is and why it exists. |
| `Benchmarks/run.md` | Instructions an AI agent follows to run the benchmark. |
| `Benchmarks/TestSolution/` | One self-contained C# solution used by every use case. It is the only folder checked out into each benchmark worktree. |
| `Benchmarks/use-cases/find_symbol/prompt.md` | The prompt sent to a measured run, in full. |
| `Benchmarks/use-cases/find_symbol/scoring.md` | The expected answer and scoring criteria for that use case. |
| `Benchmarks/use-cases/find_symbol/benchmark-audit.md` | Per-criterion scoring and per-condition tool-call tables for the most recent run of that use case. |
| `Benchmarks/benchmark-results.md` | Results table: last-run timestamp, pass % and signed time/token deltas per condition for each use case. |

## Use cases

`find_symbol` asks the agent for one symbol's declaration and every call site of one specific overload, in a solution where text search alone cannot tell the overloads and same-named methods apart. Its `prompt.md` is the exact text sent to a measured run; its `scoring.md` holds the expected answer and the criteria used to judge the reply.

## What is measured

For each condition: wall-clock task duration, input/output/total token counts, every tool call with its duration where the harness exposes one, the final answer, and correctness/completeness against the expected outcome in the use case's `scoring.md`. The record also carries the harness, model, model settings, starting commit, and the session mechanism with the location of its record.

Roslynk solution-opening duration, tokens, retries and failures are recorded separately, so the setup cost stays visible without inflating the task totals.

## Running it

A human does not run this by hand. Ask an AI agent that has access to the harness being measured to run the Roslynk benchmarks; it must follow `Benchmarks/run.md`, which defines the conditions, worktrees, measurements, record format and cleanup. It starts one session per condition in the harness itself and reads the harness's own session record; it does not write a script that drives the harness. Run records are working files: they are written outside the repository and deleted once the results table and the audit are updated.

## Interpreting results

A faster or cheaper run is not an improvement unless it also produced a correct and complete answer. `benchmark-results.md` shows each condition's pass % — the share of the criteria in `scoring.md` that the answer met — next to the time and token deltas, and the use case's `benchmark-audit.md` holds the per-criterion scoring and every tool call with its duration, outcome and result size. Treat a run that failed the task as a failed run rather than a cheap one.

## Current status

The structure and the single `find_symbol` use case exist. No benchmark run has been recorded yet, so `benchmark-results.md` contains no measurements.

## Adding use cases

The suite deliberately starts with one use case so the approach can be assessed before more are added. A future use case is a new folder `Benchmarks/use-cases/{$name}/` holding three files, all using the same `TestSolution`: `prompt.md`, which contains only the prompt and is sent to a measured run in its entirety; `scoring.md`, which holds the expected answer and scoring criteria; and `benchmark-audit.md`, which holds the per-criterion scoring, the tool-call audit and the setup calls for each run. No per-use-case setup file is needed unless the task requires preparation; if one is added it must not use Roslynk, must be applied identically in both worktrees before measurement, and its time and tokens are recorded separately from task totals.

`TestSolution` is fixed benchmark data, not product code: it must not be modified between the two conditions, and `TestSolution/scripts/reset.sh` restores it inside a benchmark worktree.
