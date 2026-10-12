# `find_implementations`

Finds the implementations or overrides of an interface, interface member or abstract member.
Implementors are grouped file -> namespace, each leaf as '<typeKind>,<typeName>,<loc>':
```
  resolvedSymbol=<name, parameter types included for a method or indexer>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace>
  \t\t\t\t<typeKind>,<typeName>,<loc>
```

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Find implementations").
