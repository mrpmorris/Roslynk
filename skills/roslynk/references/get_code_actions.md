# `get_code_actions`

Lists Roslyn's code fixes and refactorings available at a position (or selection) in a .cs, .razor
or .cshtml file. The body is one '<actionId>,<kind>,<diagnosticId> <title>' line per action
(diagnosticId is '-' for a refactoring; the free-text title trails last), capped at 50 actions.
The actionId is opaque and must be passed back verbatim to apply_code_action. Fixes are driven by
the compiler and analyzer diagnostics at that span (so analyzer ids such as IDE0005 are offered
here too); refactorings by the span itself.
A documentPath that is not a solution-compiled .cs, .razor or .cshtml document is error=NotFound.
In a .razor/.cshtml file the position must be inside C# (an @code block, expression or directive),
otherwise error=NotSupported; an action whose edits cannot be mapped back to the Razor source is
refused by apply_code_action. Analyzers do not run on Razor-generated code, so a Razor file offers
compiler-diagnostic fixes and refactorings only.
