# `apply_code_fix`

Fixes a diagnostic by id (e.g. CS0219, IDE0005) and its 1-based line/column from get_diagnostics.
Selects the diagnostic whose span contains the position, without first listing actions.

## Multiple fixes

Distinct fixes return Conflict with candidate=<actionId>,Fix,<diagnosticId> <title> per fix.
Choose by title and pass actionId to apply_code_action; do not repeat apply_code_fix.

## Errors

- NotFound: documentPath is not a solution-compiled .cs/.razor/.cshtml file, or no diagnostic
  matches the position.
- Invalid: line or column below 1.
- NotSupported: no registered fix, or an unmappable Razor edit.
- Conflict: fix produced no changes (no candidates), or Razor text mismatches.
- Stale: a target file changed on disk since loading.

Razor positions/mapping follow the shared rules. Compiler fixes and CS8019/IDE0005 are supported;
the latter remove the @using on that line. Ambiguity and mapping failures occur before saving.

Returns applied, action and status headers, then a blank line and solution-relative changed paths.
