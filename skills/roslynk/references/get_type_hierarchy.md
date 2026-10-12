# `get_type_hierarchy`

Returns a type's base-type chain, implemented interfaces and known derived types. The body has up
to three sections (base, interfaces, derived); an empty section is omitted, and each entry is a
'<typeKind>,<fully-qualified name>' where typeKind is class|struct|interface|enum|delegate:
```
  resolvedType=<fully-qualified type>

  base
  \t<typeKind>,<fully-qualified name>
  interfaces
  \t<typeKind>,<fully-qualified name>
  derived
  \t<typeKind>,<fully-qualified name>
```
The chain comes from the compiler, including base types defined in referenced assemblies.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Get a type's hierarchy").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
