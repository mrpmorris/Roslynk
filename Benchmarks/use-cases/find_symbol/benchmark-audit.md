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
| Call site 3 | fail | pass |
| No false positives | fail | pass |
| Overall | fail | pass |

## Tool call audit

Times are the gaps between the issuing assistant turn and its tool result, not tool-internal latency. Result size is the stored result's character count.

### Standard tools

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 402 ms | ok | 229 |
| search_files | 1.3 s | ok | 1001 |
| read_file | 914 ms | ok | 1772 |
| read_file | 945 ms | ok | 425 |
| read_file | 950 ms | ok | 616 |
| read_file | 893 ms | ok | 1223 |
| read_file | 899 ms | ok | 1008 |
| read_file | 907 ms | ok | 948 |
| terminal | 472 ms | ok | 2161 |
| read_file | 1.0 s | ok | 1085 |
| read_file | 1.0 s | ok | 656 |
| read_file | 1.0 s | ok | 850 |
| read_file | 1.1 s | ok | 711 |

### Roslynk

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 450 ms | ok | 170 |
| mcp__roslynk__get_solution_status | 475 ms | ok | 505 |
| mcp__roslynk__search_symbols | 12 ms | ok | 1590 |
| mcp__roslynk__search_symbols | 1.8 s | ok | 788 |
| mcp__roslynk__get_symbol_body | 38 ms | ok | 1302 |
| mcp__roslynk__get_symbol_body | 285 ms | ok | 699 |
| mcp__roslynk__get_symbol | 54 ms | ok | 485 |
| mcp__roslynk__get_symbol | 42 ms | ok | 508 |
| mcp__roslynk__find_references | 605 ms | ok | 910 |
| read_file | 956 ms | ok | 444 |
| read_file | 987 ms | ok | 442 |
| read_file | 991 ms | ok | 388 |
| read_file | 997 ms | ok | 1129 |
| mcp__roslynk__find_references | 47 ms | ok | 911 |
| terminal | 432 ms | ok | 1518 |
