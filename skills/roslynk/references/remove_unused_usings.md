# `remove_unused_usings`

Removes unnecessary using directives (the compiler's CS8019) across the solution, or in one file
when documentPath is given. .razor and .cshtml files are included: an unnecessary @using line
written in the file is removed, while directives from imports files (_Imports.razor,
_ViewImports.cshtml) are left alone because other components share them. A documentPath that is
not a solution-compiled .cs, .razor or .cshtml document is error=NotFound; a file edited on disk
since it was loaded is error=Stale. With nothing to remove the call still succeeds with applied=N
and removedCount=0 (not an error, and safe to re-run).
Returns 'applied', 'removedCount', 'status' header, a blank line, then one solution-relative
changed-file path per line.

Annotations: read-only no, idempotent yes, destructive yes, open-world no (published MCP title: "Remove unused usings").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
