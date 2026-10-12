# `search_symbols`

Searches source-declared symbols whose name contains the query (case-insensitive), across the
solution, declarations a source generator emits included. Local functions are included, nesting
under the member (and any outer local functions) that declares them. Matches are grouped file ->
namespace -> type -> member, a matched member nesting under its (parent-only) type:
```
  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace>
  \t\t\t\t<typeKind>,<typeName>,<loc>
  \t\t\t\t\t<memberKind>,<memberName>,<loc>
```
A type's location is present only when the type itself matched.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Search symbols by name").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
