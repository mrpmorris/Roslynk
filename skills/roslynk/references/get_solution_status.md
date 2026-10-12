# `get_solution_status`

Lists the solutions currently loaded by the server: a blank line, then one line per solution
'<solutionId>,<status>,<loaded>/<total>', where loaded is how many projects have loaded so far (a
live count while still Building) and total is the count once known ('?' until the first load
finishes). A solution whose load reported messages (a skipped or unloadable analyzer or generator,
a project that failed to evaluate, an SDK that was not found; often the cause of confusing compiler
errors) is, once it is Ready (never while Building, Updating or Faulted), followed by one indented
'loadDiagnostic=<message>' line per message, so a solution with none stays a single line. At most
20 messages are listed per solution, then an indented
'loadDiagnosticsTruncated=<n>' line counts the rest; pass allLoadDiagnostics=true to list every
message.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Get loaded-solution status").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
