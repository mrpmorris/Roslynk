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

### Standard tools

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| search_files | 2429 ms | ok | 1992 bytes |
| search_files | 2437 ms | ok | 376 bytes |
| read_file | 1174 ms | ok | 1772 bytes |
| read_file | 1183 ms | ok | 425 bytes |
| read_file | 1191 ms | ok | 1223 bytes |
| read_file | 1195 ms | ok | 1008 bytes |
| read_file | 1200 ms | ok | 948 bytes |
| read_file | 1205 ms | ok | 616 bytes |
| search_files | 1031 ms | ok | 1980 bytes |
| search_files | 1055 ms | ok | 2525 bytes |
| read_file | 1915 ms | ok | 711 bytes |
| read_file | 1924 ms | ok | 619 bytes |
| read_file | 1943 ms | ok | 656 bytes |
| search_files | 1948 ms | ok | 2236 bytes |

### Roslynk

| Tool name | Time | Outcome | Result size |
| --- | --: | --- | --: |
| terminal | 468 ms | ok | 119 bytes |
| tool_describe | 570 ms | ok | 6275 bytes |
| mcp__roslynk__open_solution | 35 ms | ok | 693 bytes |
| mcp__roslynk__search_symbols | 97 ms | ok | 788 bytes |
| read_file | 628 ms | ok | 1772 bytes |
| read_file | 634 ms | ok | 425 bytes |
| mcp__roslynk__get_callers | 74 ms | ok | 907 bytes |
| terminal | 468 ms | ok | 1561 bytes |
| read_file | 838 ms | ok | 1223 bytes |
| read_file | 843 ms | ok | 1008 bytes |
| read_file | 847 ms | ok | 948 bytes |
