# `apply_code_fix`

Applies the code fix for the diagnostic with this id (a compiler id such as CS0219, or an analyzer
id such as IDE0005) at line:column in a .cs, .razor or .cshtml file - the quick path when
get_diagnostics has already given the id and position, without first listing actions. Pass the
1-based line and column get_diagnostics printed for that entry; the diagnostic whose span contains
that position is fixed. When the diagnostic has more than one distinct fix (for example several
namespaces to import, or make-nullable versus add-required), nothing is written and the result is
error=Conflict with one 'candidate=<actionId>,Fix,<diagnosticId> <title>' header per fix: choose
the fix whose title matches your intent and pass its actionId to apply_code_action; do not call
apply_code_fix again for it.
Errors: a documentPath that is not a solution-compiled .cs, .razor or .cshtml document, or no such
diagnostic at that position, is error=NotFound; a line or column below 1 is error=Invalid; a
diagnostic with no registered fix is error=NotSupported; a fix that produced no changes is
error=Conflict (without candidates). In a .razor/.cshtml file the position is in the Razor file,
the fix is computed on the generated C# and mapped back to the Razor source: an unmappable edit is
error=NotSupported, mismatched Razor text error=Conflict, nothing written. Analyzers do not run on
Razor-generated code, so Razor supports compiler diagnostics, plus CS8019/IDE0005, which remove
the unnecessary @using on that line. A file edited on disk since it was loaded is error=Stale.
Returns 'applied', 'action', 'status' header, a blank line, then one solution-relative
changed-file path per line.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Apply a diagnostic's fix").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
