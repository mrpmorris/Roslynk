# `change_signature`

Adds an optional parameter to one ordinary method/local function. A default value is required.
If supplied, the call-site value is threaded through invocations as a named argument.
Method groups, nameof and cref references remain unchanged.

## Limits and errors

- NotSupported: virtual/override/abstract methods, interface members/implementations, partial
  methods, params methods, constructors or generated-only declarations (names the generator).
- Invalid: invalid parameter name, type or default value.
- Ambiguous: multiple overloads; returns one candidate per match.
- Stale: a target file changed on disk since loading.

Supports declarations in Razor @code and calls in markup under the shared mapping rules:
NotSupported for unmappable edits, Conflict for mismatched source, before saving.

Returns applied, resolvedMethod (pre-edit overload with parameter types), updatedCallSites and
status headers, then a blank line and solution-relative changed paths.
