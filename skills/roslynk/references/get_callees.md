# `get_callees`

SupportsPaging=Y

Finds the methods, constructors, property or event accessors and operators that the named member's
code calls.
Callees in the solution are grouped like get_callers; callees declared in a referenced assembly
(BCL, NuGet) are grouped under one top-level '<external:AssemblyName>' line per assembly instead
of a project, with an empty loc and a pipe-delimited parameterTypes field only when the callee's
own type declares more than one method of that name (inherited overloads do not count; '()' for
the parameterless overload):
```
  resolvedSymbol=<name, parameter types included for a method or indexer>

  <external:AssemblyName>
  \t<namespace>
  \t\t<typeKind>,<typeName>
  \t\t\t<memberKind>,<memberName>,[,<parameterTypes>]
  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace>
  \t\t\t\t<typeKind>,<typeName>
  \t\t\t\t\t<memberKind>,<memberName>,<loc>[,<parameterTypes>]
```
Each callee is reported as declared: generic instantiations (List<int>.Add, To<string>) collapse
to the generic definition, and an extension method lists its 'this' parameter.
The member may be a method, constructor, operator, property, indexer, event or field. A property
read or write names the accessor that runs (get or set; a compound assignment or increment reads
and writes, so both appear), and 'e += h' names the event's add accessor. User-defined operators
and conversions, the Dispose a using runs, and a method group passed as a delegate are included.
The plumbing the compiler inserts for foreach (GetEnumerator, MoveNext, Current) and await
(GetAwaiter, IsCompleted, GetResult) is not; an explicit .GetAwaiter() call is. A constructor also
reports the calls in the field and property initializers it runs. A property is its accessors, a
field its initializer. Lambdas and local functions declared inside the member belong to it. A
member with no code in the solution (an abstract or interface declaration, an auto-property)
calls nothing.
