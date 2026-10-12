# `multi_query`

Executes several read-only operations in one call, against a single consistent snapshot of the
solution, and returns one result per delivered operation in request order. Operations are
independent - no operation may reference another's output.
When you need several facts before making a change, send them as one multi_query call rather than as
sequential calls.
For usage/impact questions, batch find_references, get_callers, get_callees,
find_implementations and get_type_hierarchy in one call.
Read-only query tools only: get_symbol, get_symbol_body, get_members, find_definition,
find_implementations, find_references, get_callers, get_callees, search_symbols, get_type_hierarchy, find_dead_code,
find_dead_conditionals, get_expression_info. Write tools, get_diagnostics and get_solution_status are not multi-queryable;
get_diagnostics and get_solution_status are ordinary single calls.
Each operation takes exactly that tool's single-call parameter names; unknown or misspelled parameters
are rejected rather than ignored, and omitted parameters take the tool's declared defaults.
The whole response stays within the server's response budget (about 80,000 characters by default; set
Roslynk:MaxResponseChars to change it - the envelope echoes the per-operation allowance as budget=<n>).
Every operation runs; results are then delivered WHOLE, smallest first, until the budget is filled -
a slot is complete or absent, never cut. Operations whose result did not fit are listed once in the
header as retry=<index>:<size>, in request order, where size is that result's cost as a ratio of the
budget rounded up to 0.1 (0.1 is the smallest value reported, never 0.0). Plan the continuation from
those ratios: a size of 1.0 or more can never fit beside anything, so re-send that operation on its
own (it will page with nextStartItem=); sizes summing to at most 1.0 fit one continuation batch
together; ceil(size) is roughly the number of pages a solo call needs. Sizes smaller than 0.1 of the
budget are indistinguishable, so about ten such operations per continuation batch is the safe number.
Re-send exactly the operations listed, with their original arguments, from your own copy of the
request - slot frames carry the index and tool name only, never the arguments. Pass the response's
snapshot=<id> as expectSnapshot when you continue: a continuation that would straddle two
generations of the solution (a write, or the user saving a file) is refused with error=Stale instead
of silently mixing them - on Stale, re-run the whole batch. Ratios describe one snapshot only; a
re-run against a moved solution re-measures and re-decides.
Trust rule: a delivered slot is always a complete result, so only a slot body's own headers may drive
tool-level paging (nextStartItem=); nothing in a multi_query response is ever cut mid-body.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Run several read-only queries in one call").
