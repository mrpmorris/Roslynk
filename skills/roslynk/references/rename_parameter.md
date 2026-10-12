# `rename_parameter`

Renames a parameter of one method, local function, constructor or indexer overload. Updates its
declaration, body uses, named call arguments and <paramref> documentation; leaves strings,
comments and unrelated same-named text unchanged.

## Related members

Cascades through overridden/overriding members, interfaces/implementations and both partial-method
parts when a source parameter at the same position has the same old name. Different names,
metadata declarations and generated-only declarations remain unchanged and appear in
unchangedRelated.

## Result

Headers, then a blank line and one solution-relative changed-file path per line:

- applied: N for checkOnly previews.
- resolvedMethod: resolved member before rename; parameter: old parameter name.
- renamedMembers: renamed declaration count, including the target.
- unchangedRelated: semicolon-separated unchanged related members; omitted when empty.
- status: follows the shared output protocol.

## Errors and recovery

- Invalid: invalid identifier or newName equals parameterName.
- Ambiguous methodId: one candidate per overload. No match: fuzzy suggestions.
  Copy a candidate back verbatim.
- NotFound: parameterName absent; lists the member's parameters.
- Conflict: newName clashes with a parameter, local, local function, lambda/query variable
  or type parameter inside a renamed declaration.
- NotSupported: target is not a method/local function/constructor/indexer, is a positional
  record parameter, or is declared only in metadata or generated source (names the generator).
- Stale: a file changed on disk since loading.

Nothing is written in any failure case.
