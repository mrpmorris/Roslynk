# `rename_symbol`

Renames a symbol and references across partial classes, code-behind and loaded frameworks,
subject to shared conditional-coverage limits. Leaves strings and comments unchanged.

## Razor and generated declarations

Shared Razor mapping rules cover @code, markup expressions and component attributes. Unverified/unmappable edits abort before saving (Conflict or NotSupported).

Generated-only declarations return NotSupported naming the generator; change the generator or
its input instead. Partial members/types with handwritten declarations are renamable because
the generator follows their names.

## Result and errors

Returns applied (N for checkOnly), resolvedSymbol (pre-rename name) and status headers,
then a blank line and solution-relative changed paths.

- Invalid: invalid identifier.
- Ambiguous: one candidate per match. No match: fuzzy suggestions. Reuse candidates verbatim.
- Stale: a target changed on disk or in the model since computation; names the file.
  Intervening edits to other files are preserved. Stale detection occurs before saving.
