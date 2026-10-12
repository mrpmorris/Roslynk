# `remove_unused_usings`

Removes unnecessary using directives (CS8019) solution-wide or in documentPath.
Includes .razor/.cshtml @using lines; preserves shared _Imports.razor/_ViewImports.cshtml usings.

Returns applied, removedCount and status headers, then a blank line and solution-relative
changed paths. With nothing to remove: applied=N, removedCount=0; safe to rerun.

NotFound: documentPath is not a solution-compiled .cs/.razor/.cshtml file.
Stale: a target file changed on disk since loading.
