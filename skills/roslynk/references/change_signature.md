# `change_signature`

Adds a new optional parameter to a method and, if a call-site value is given, threads it through
every invocation as a named argument; the parameter must have a default so the change stays
backward-compatible. v1 targets a single ordinary method or local function only: it refuses
virtual/override/abstract methods, interface members and their implementations, partial methods,
params methods, constructors, and a method declared only by a source generator (error=NotSupported
naming the generator: the declaration is never written to disk and would be regenerated unchanged).
Only true invocation call sites are updated - method groups, nameof and cref references are left
alone (the parameter is optional, so they remain valid). Methods declared in .razor/.cshtml @code
blocks and call sites in Razor markup are supported: edits are mapped back to the Razor source,
and an edit that cannot be mapped is error=NotSupported (or error=Conflict when the Razor text no
longer matches), with nothing written.
Returns 'applied', 'resolvedMethod' (the name with its parameter types, identifying which overload
was changed), 'updatedCallSites', 'status' header, a blank line, then one solution-relative
changed-file path per line.
Errors: an invalid parameter name, type or default value is error=Invalid; a bare name that
matches several overloads is error=Ambiguous with one candidate per match; a file edited on disk
since it was loaded is error=Stale.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Add a parameter to a method").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
