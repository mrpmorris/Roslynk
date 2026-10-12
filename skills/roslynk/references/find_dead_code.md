# `find_dead_code`

Finds symbols (types, methods, properties, fields, events) with no references, conservatively
filtered to avoid false positives: it excludes interface implementations, virtual/override chains,
test members, generated code, and DI/reflection-activated members, and (unless includePublic is
true) the public API surface. Also use find_dead_conditionals.
Candidates nest file -> namespace -> type -> member; a dead type is itself a leaf carrying its own
loc; confidence is High or Medium; the free-text reason is the next field:
```
  <project>
  \t<relative/forward-slash/folder>
  \t\t<file.cs|file.razor>
  \t\t\t<namespace>
  \t\t\t\t<typeKind>,<typeName>
  \t\t\t\t\t<memberKind>,<memberName>,<loc>,<confidence>,<reason>
```
The loc is the full declaration span, ready to pass to apply_patch to remove the member. Each
unused overload of a member is reported as its own leaf, distinguished by its loc; the leaf name
carries no parameter list, so pass the member name to get_symbol_body and pick from the
candidates it offers. The host decides whether to remove a candidate.
The scan is per-symbol, so narrow large solutions with scope. maxResults caps the scan itself
(count=<candidates found> and truncated=Y appear when the cap cut the scan short, and
nextStartItem=<k> appears only when a page of the result does not show all of them); omit it to
scan everything (the result then pages).
