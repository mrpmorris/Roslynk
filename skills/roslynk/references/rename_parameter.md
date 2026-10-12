# `rename_parameter`

Renames one parameter of one method, local function, constructor or indexer overload: the
declaration, every use in the body, named arguments at call sites and <paramref> documentation
references are updated; string literals, comments and same-named text elsewhere are left untouched.
The rename cascades across the member's override/interface group - overridden and overriding
members, interface members and their implementations, and both parts of a partial method -
wherever the related declaration is in source and its parameter at the same position has the same
old name. Related declarations that use a different name, that live in metadata, or that are
declared only by a source generator are left as they are and reported in 'unchangedRelated'.
Returns 'applied' (N for a checkOnly preview), 'resolvedMethod' (the member as resolved, before
the rename), 'parameter' (the old name), 'renamedMembers' (how many declarations had the
parameter renamed, including the target), 'unchangedRelated' (semicolon-separated related members
left alone; omitted when there are none), 'status' header, a blank line, then one
solution-relative changed-file path per line.
Errors: an invalid identifier, or a newName equal to parameterName, is error=Invalid; an ambiguous
methodId returns one candidate= line per overload and a methodId that matches nothing returns
fuzzy suggestions (copy a candidate back verbatim); a parameterName the member does not declare is
error=NotFound listing its parameters; a newName already used by another parameter, local, local
function, lambda/query variable or type parameter inside a renamed declaration is error=Conflict;
a target that is not a method, local function, constructor or indexer, a positional record
parameter, a member declared only in metadata, or a member declared only by a source generator is
error=NotSupported (naming the generator in the last case); a file edited on disk since it was
loaded is error=Stale. Nothing is written in any failure case.

Annotations: read-only no, idempotent no, destructive yes, open-world no (published MCP title: "Rename a method parameter").

---

Shared guidance - result grammar, name matching, paging, freshness, failure codes, the
edit/diagnostic workflow and Razor handling - is in [SKILL.md](../SKILL.md). Load it once,
not per tool.
