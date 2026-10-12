# `get_callers`

Finds the methods that call the named method. Calls the compiler inserts count too: foreach/await
plumbing (GetEnumerator, MoveNext, Current, an enumerator's Dispose, await foreach and await using
equivalents, GetAwaiter, IsCompleted, GetResult), the Dispose/DisposeAsync a using or await using
runs, deconstruction and positional patterns, collection-initializer Add, user-defined operators
and conversions (including +=, ++, implicit conversions and true/false in a condition),
query-expression methods, interpolated-string handler members, implicit base() calls, and the
Length/Count behind ^ and ...
Callers are grouped file -> namespace -> containing type -> calling member, each leaf showing the
caller's declaration location:
```
  resolvedSymbol=<name, parameter types included for a method or indexer>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace>
  \t\t\t\t<typeKind>,<typeName>
  \t\t\t\t\t<memberKind>,<memberName>,<loc>
```

Annotations: read-only yes, idempotent yes, destructive no, open-world no (published MCP title: "Get callers of a method").
