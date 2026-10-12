# `extract_method`

Extracts a contiguous selection of C# statements or a single expression into a new method (or
local function) and replaces the selection with a call, using Roslyn's own Extract Method
refactoring and data-flow analysis to decide parameters, ref/out, return value, static/async
modifiers and the call site; the deterministic equivalent of picking 'Extract method' from
get_code_actions. Surrounding whitespace in the selection is ignored.
Returns 'applied', 'method' (the final name), 'symbolName' (the extracted method's full name, e.g.
'N.T.M(int).NewMethod(string)' for a local function, ready to pass to the name-based tools),
'kind' (Method or LocalFunction), 'signature' (the new declaration's header) and 'call' (the
statement now containing the call) headers, 'status', a blank line, then one solution-relative
changed-file path per line.
Before anything is written the result is checked: a selection Roslyn cannot extract, or whose
extraction Roslyn flags as possibly changing behavior, is error=NotSupported with the reason; an
extraction that would introduce compile errors is error=NotSupported listing them; a methodName
that would bind a call to a different member is error=Conflict; a file edited since the extraction
was computed is error=Stale. Nothing is written in any failure case. A documentPath that is not a
solution-compiled .cs, .razor or .cshtml document is error=NotFound; generated code (.g.cs,
including Razor output) is error=NotSupported. In a .razor/.cshtml file the selection must be
inside C# (an @code block or expression), otherwise error=NotSupported; the extraction is computed
on the generated C# and mapped back to the Razor source, and an edit that cannot be mapped (such
as a method inserted outside the @code block) is error=NotSupported, mismatched Razor text
error=Conflict; an out-of-range or empty selection, or an invalid methodName, is error=Invalid.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Extract a selection into a method").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
