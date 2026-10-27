# Benchmark results

Results are regenerated from the raw run records in `Benchmarks/run-records/` by following `Benchmarks/run.md`. Every delta is **Roslynk minus Standard**, written with an explicit sign (`+` for more, `-` for less; zero may be `0`). Time carries its unit. A negative value means Roslynk used less time or fewer tokens. Read the deltas next to the correctness result: a faster or cheaper run is not an improvement if it failed the task.

No run has been recorded yet, so every cell is `unavailable`. This table is the structure a run fills in; it is not a measurement.

| Test | Standard time | Roslynk time | Roslynk time delta | Standard tokens in | Roslynk tokens in | Roslynk tokens delta | Standard tokens out | Roslynk tokens out | Roslynk tokens out delta | Standard total tokens | Roslynk total tokens | Roslynk total tokens delta |
| --- | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: | --: |
| find_symbol | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable | unavailable |

## Correctness

| Test | Standard result | Roslynk result |
| --- | --- | --- |
| find_symbol | unavailable | unavailable |

## Tool-call audit

One entry per tool call, in call order. Setup calls (`open_solution` and solution-status polling) are listed separately and are excluded from task totals.

- find_symbol
  - Standard
    - No run recorded.
  - Roslynk
    - No run recorded.

## Setup (excluded from task totals)

- find_symbol
  - Roslynk
    - No run recorded.

## Provenance

| Field | Value |
| --- | --- |
| Harness | unavailable |
| Model | unavailable |
| Model settings | unavailable |
| Starting commit | unavailable |
| Session records | unavailable |
| Run records | unavailable |

## Limitations

No run has been recorded, so there are no harness limitations to report yet. A run must record any measurement its harness does not expose as `unavailable` rather than inventing a value.
