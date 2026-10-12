# `find_writes`

SupportsPaging=Y

Finds the writes (assign, compound, increment, ref, out and init) of a field, property or
parameter across the solution, resolved semantically.
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
Access kinds:

- read; assign: simple '='.
- compound: '+=', '??=', etc.; increment: '++'/'--'.
- ref: passed by ref, 'ref x', '&x'; out: passed as out.
- init: declaration, object/with initializer, attribute named argument, or assignment to
  a constructor's own field/property in that constructor.

compound, increment and ref appear in both find_reads and find_writes; do not sum their counts.
nameof, documentation crefs and named call arguments ('M(value: 1)') are excluded.
error=NotSupported is returned for a symbol that cannot be read or written (a method, type, event
or local variable).
