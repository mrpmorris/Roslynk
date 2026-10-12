# `get_members`

Lists a type's members (methods, properties, fields, events, nested types), grouped by the file
that declares them (or '<metadata>' for a referenced assembly):
```
  resolvedType=<fully-qualified type>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<memberKind>,<name>,<loc>,<paramType|paramType|...>
  \t\t\t\tlocalfunction,<name>,<loc>,<paramType|...>
```
A member's local functions are listed beneath it in declaration order, one level deeper per
nesting. The loc is empty for a metadata member; the trailing signature is a pipe-delimited list
of minimally-qualified parameter types, present only for methods that take parameters. All
accessibilities including private are listed; inherited members are excluded unless
includeInherited. Use get_symbol_body to read a member's body. Narrow a large type with
nameFilter (a trailing '*' matches by prefix, otherwise a case-insensitive substring) and the
include* kind toggles.

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Get a type's members").
