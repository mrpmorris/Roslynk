# `apply_patch`

Applies a git unified diff to text files, located by content (not line numbers) and written
atomically. Targets: solution-compiled .cs files and .razor/.cshtml documents (kept in sync with
the in-memory model), plus any other existing text file under the solution folder (e.g. .csproj,
.json, .md), written to disk. Returns header-only 'applied=<Y|N>' (and 'status' only when not
Ready) on success (applied is N for a checkOnly preview).
Hunk headers may omit line numbers (a bare '@@'); a content-anchored hunk must match exactly one
place, so include enough surrounding context that it is unambiguous. Paths are relative to the
solution folder; a path that is a unique suffix of a solution file (e.g. relative to the
repository root) also resolves. Edits existing files only: creation/deletion, binary files and
paths outside the solution folder are error=NotSupported; a file that does not exist is
error=NotFound; a path matching several distinct files is error=Ambiguous with
'candidate=<solution-relative path>' lines. Rejections list 'rejected=<path>' lines. Pass
baseVersions (the documentVersion each file was read at) to be told if a file moved since
(error=Stale with 'stale=<path>' lines). An unmatched or ambiguous hunk is error=Conflict naming
the file and reason; a patch with no file sections is error=Invalid.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Apply a patch").
