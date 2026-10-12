# `apply_code_action`

Applies an actionId from get_code_actions. Re-resolves the action at its original position;
no action state is retained between calls.

- Conflict: action no longer resolves after edits; rerun get_code_actions.
- Invalid: malformed actionId.
- Stale: a target file changed on disk since loading.

Razor actions use the shared mapping rules. Adding usings/members to generated scaffolding may
be unmappable (NotSupported); mismatched Razor text returns Conflict, before saving.

Returns applied, action and status headers, then a blank line and solution-relative changed paths.
