# `find_reads`

Finds the reads of a field, property or parameter - including the read half of compound, increment
and ref - across the solution, resolved semantically, so same-named symbols in other scopes are
never reported.
The body has the find_references shape with one location per leaf and the access kind appended:
```
  resolvedSymbol=<name; a parameter is written Member:parameter>

  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace, or "<global>">
  \t\t\t\t<typeKind>,<typeName>
  \t\t\t\t\t<memberKind>,<memberName>,<loc>,<accessKind>
```
accessKind is one of read, assign (simple '='), compound ('+=', '??=' and friends), increment ('++'/'--'), ref (passed by ref, 'ref x', or '&x'), out (passed as out), init (a declaration initialiser, an object or 'with' initialiser, an attribute named argument, or an assignment to the constructor's own field/property in its constructor). compound, increment and ref both read and write: each such location appears in BOTH find_reads and find_writes with the same accessKind, so do not add the two tools' counts together. nameof(...), doc-comment crefs and named arguments ('M(value: 1)') are neither, and are omitted.
error=NotSupported is returned for a symbol that cannot be read or written (a method, type, event
or local variable).
Coverage: every loaded target framework, plus derived compilations each toggling ONE preprocessor symbol
