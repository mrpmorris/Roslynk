# find_symbol audit

## Per criterion scoring

Criteria are defined in scoring.md

| Criterion | Standard | Roslynk |
| --- | --- | --- |
| Declaration path | pass | pass |
| Declaration line | pass | pass |
| Signature | pass | pass |
| Call-site count | pass | fail |
| Call site 1 | pass | pass |
| Call site 2 | pass | fail |
| Call site 3 | pass | pass |
| No false positives | pass | pass |
| Overall | pass | fail |

## Tool call audit

### Standard tools

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 0.7 s | ok |  |
| terminal | 0.5 s | ok |  |
| terminal | 0.5 s | ok |  |
| terminal | 0.5 s | ok |  |
| terminal | 0.4 s | ok |  |
| terminal | 0.5 s | ok |  |

### Roslynk

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| tool_search | 4.2 s | ok |  |
| search_files | 1.0 s | ok |  |
| read_file | 1.2 s | ok |  |
| read_file | 1.2 s | ok |  |
| read_file | 1.2 s | ok |  |
| read_file | 1.1 s | ok |  |
| search_files | 1.9 s | ok |  |
