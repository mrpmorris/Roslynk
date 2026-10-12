# `apply_code_action`

Applies a code action discovered by get_code_actions, identified by its actionId. The action is
re-resolved at the same position (nothing is held between calls): if the code changed since
discovery, the re-resolution no longer finds the action and the result is error=Conflict - re-run
get_code_actions and pick again; a malformed actionId is error=Invalid.
For an action discovered in a .razor/.cshtml file the edits are mapped back to the Razor source;
an edit that cannot be mapped (for example a using or member added to generated scaffolding) is
error=NotSupported, mismatched Razor text error=Conflict, and nothing is written. A file edited on
disk since it was loaded is error=Stale.
Returns 'applied', 'action', 'status' header, a blank line, then one solution-relative
changed-file path per line.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Apply a code action").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
