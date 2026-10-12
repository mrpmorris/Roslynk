# `find_references`

SupportsPaging=Y

Finds semantic references across the solution, including partial classes and code-behind.
The body groups every reference under file -> namespace -> type -> member, so a shared declaration
is printed once:
```
  resolvedSymbol=<name, parameter types included for a method or indexer>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace, or "<global>">
  \t\t\t\t<typeKind>,<typeName>,<loc|loc|...>
  \t\t\t\t\t<memberKind>,<memberName>,<loc|loc|...>
```
The type line carries locations only when the type declaration itself references the symbol.
Combine with get_callers/get_callees/find_implementations/get_type_hierarchy via multi_query for
usage/impact analysis.
