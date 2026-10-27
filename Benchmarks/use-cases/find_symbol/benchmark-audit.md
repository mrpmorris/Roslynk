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

Times are the gaps between the issuing assistant turn and its tool result, not tool-internal latency; calls issued in one turn share a start, so their times overlap. Result size is the stored result's character count.

### Standard tools

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| search_files | 2.3 s | ok | 1650 chars |
| search_files | 2.3 s | ok | 262 chars |
| read_file | 1.2 s | ok | 1772 chars |
| read_file | 1.2 s | ok | 1223 chars |
| read_file | 1.2 s | ok | 1008 chars |
| read_file | 1.2 s | ok | 948 chars |
| search_files | 1.1 s | ok | 1754 chars |
| search_files | 1.1 s | ok | 831 chars |
| search_files | 1.9 s | ok | 1650 chars |
| search_files | 2.0 s | ok | 1521 chars |
| read_file | 1.2 s | ok | 619 chars |
| read_file | 1.2 s | ok | 711 chars |
| read_file | 1.2 s | ok | 656 chars |
| read_file | 1.3 s | ok | 425 chars |

### Roslynk

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| skill_view | 202 ms | ok | 39428 chars |
| tool_describe | 212 ms | ok | 14219 chars |
| mcp__roslynk__open_solution | 1.6 s | ok | 639 chars |
| mcp__roslynk__search_symbols | 2.1 s | ok | 788 chars |
| mcp__roslynk__get_members | 2.4 s | ok | 767 chars |
| mcp__roslynk__get_symbol_body | 53 ms | ok | 569 chars |
| mcp__roslynk__find_references | 599 ms | ok | 910 chars |
| mcp__roslynk__multi_query | 73 ms | ok | 2333 chars |
| terminal | 672 ms | ok | 1520 chars |
