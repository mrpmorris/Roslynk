# `multi_query`

Runs independent read-only operations against one solution snapshot; delivered results are in
request order. Operations cannot reference each other's output. Batch several facts needed before
a change rather than making sequential calls.
Batch only the independent queries needed to answer the question.

## Operations

Supports get_symbol, get_symbol_body, get_members, find_definition, find_implementations,
find_references, get_callers, get_callees, search_symbols, get_type_hierarchy, find_dead_code,
find_dead_conditionals and get_expression_info.

Use each tool's single-call parameter names. Unknown/misspelled parameters are rejected;
omitted parameters use that tool's defaults.

## Budget and continuation

The response budget defaults to about 80,000 characters; configure Roslynk:MaxResponseChars.
The envelope reports the per-operation allowance as budget=<n>.
All operations run. Whole results are selected smallest-first until the budget is filled;
a slot is complete or absent, never cut. Missing results appear once as retry=<index>:<size>
in request order. size is result cost / budget, rounded up to 0.1 (minimum 0.1).

- size >= 1.0: resend the operation alone.
- Sizes totaling <= 1.0: fit in one continuation batch.
- Sizes below 0.1 are indistinguishable; about ten per continuation batch is safe.

Resend exactly the retry operations with their original arguments from your request copy;
slot frames contain only index and tool name. Pass snapshot=<id> as expectSnapshot.
A changed generation (write or user save) returns error=Stale; rerun the whole batch.
Ratios apply only to that snapshot; rerunning remeasures and reselects results.

## Paging exception

Delivered slots are complete; only each slot body's own nextStartItem drives tool-level paging.
ceil(size) estimates the number of pages a solo call needs.
