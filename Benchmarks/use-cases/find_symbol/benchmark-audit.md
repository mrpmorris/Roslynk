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
| Call site 2 | pass | pass |
| Call site 3 | pass | fail |
| No false positives | pass | fail |
| Overall | pass | fail |

## Tool call audit

### Standard tools

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 443 ms | ok | 119 bytes |
| search_files | 1357 ms | ok | 926 bytes |
| read_file | 659 ms | ok | 1772 bytes |
| read_file | 666 ms | ok | 425 bytes |
| search_files | 791 ms | ok | 2431 bytes |
| read_file | 873 ms | ok | 1223 bytes |
| read_file | 880 ms | ok | 1008 bytes |
| read_file | 886 ms | ok | 948 bytes |
| terminal | 449 ms | ok | 776 bytes |
| terminal | 390 ms | ok | 704 bytes |

### Roslynk

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 488 ms | ok | 287 bytes |
| search_files | 1466 ms | ok | 2623 bytes |
| read_file | 1039 ms | ok | 1772 bytes |
| read_file | 1071 ms | ok | 1223 bytes |
| read_file | 1128 ms | ok | 948 bytes |
| read_file | 1148 ms | ok | 1008 bytes |
