# `get_code_actions`

Lists fixes/refactorings at a position or selection in a solution-compiled .cs/.razor/.cshtml
file. Fixes use compiler/analyzer diagnostics at the span; refactorings use the span itself.

Returns at most 50 <actionId>,<kind>,<diagnosticId> <title> lines. diagnosticId is '-' for
refactorings; title is trailing free text. Pass opaque actionIds verbatim to apply_code_action.

Unknown documentPath returns NotFound. Razor positions must be in C# (@code, expressions or
directives), otherwise NotSupported. Razor offers compiler fixes/refactorings, not analyzer fixes;
apply_code_action rejects edits that cannot map back to source.
