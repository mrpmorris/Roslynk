---
name: roslynk
description: Use Roslynk for C#/.NET code review, investigation, compiler diagnostics, symbol navigation, and semantic edits when its MCP server is available. Applies to .cs, .razor, and .cshtml code — checking compilation, finding definitions, references, callers or implementations, renaming, applying fixes or refactorings, and dead-code analysis — even when the user never names Roslynk. Prefer it over grep, file reads, hand edits, and dotnet build for those questions.
---

# Roslynk

Use Roslynk for semantic inspection and editing of *.cs, *.razor and *.cshtml files.

Read this guide once per session, then read each tool's linked reference before first use.
Read only references needed for the task. Inspect MCP schemas only when needed to investigate
tool metadata or parameters.

## Scope

Roslynk only sees files compiled in the solution. When a tool reports a file as not found, you may
use standard tools to read or edit that file, and re-check each time in case it is later added to a
project. Standard tools may also inspect diffs, non-code files (.csproj, .json, .md) and bounded
source excerpts at locations Roslynk has already reported.

## Lifecycle: opening, indexing and reloading

open_solution returns a solutionId; every other tool needs it. While a solution loads, tools
report error=Indexing: retry shortly, or poll get_solution_status (a load finishes in seconds to
a minute). The file watcher folds edits, including your own, so never call reload_solution on
your own initiative; suggest it to the user when the model may be stale (branch switch, dotnet
restore, SDK or Directory.Build.props change).

## Output protocol

Results are compact text, not JSON: 'key=value' header lines, a blank line, then a tab-indented
body (no blank line means all headers; status= appears only when the solution is not Ready).
Newlines are '\n'; booleans are Y or N. A parameter whose (Default value: ...) matches your intent
may be omitted. A leaf is 'kind,name,loc[,extra]': kind is one of
method|localfunction|property|field|event|class|struct|interface|enum|delegate; loc is
line:col or startLine:startCol-endLine:endCol, several locations pipe-delimited (loc|loc|...); a
value containing a comma is single-quoted; extra fields follow loc in the documented order. Paths
are solution-relative. A path-bearing result labels the owning project (project file name, .csproj
omitted, other extensions kept): a #project=<project> header beside a single #path, or the
outermost node above the path in a nested body, absent when the result has no source (a metadata
symbol). In a nested body the path is split into a folder line with the file name nested beneath
it. A location declared by a source generator has a virtual path marked generated=Y
(generator=<type>): read that code with get_symbol_body, not from disk.

## Name grammar

Pass exactly the fully-qualified names tools return; on error=Ambiguous or error=NotFound the
response lists candidate= names the same tool accepts - send one back verbatim. A method or indexer
may carry a parameter-type list to select one overload ('Ns.T.M(int, string)',
'Ns.T.this[int]'); parameter names, defaults and nullability are ignored and fully-qualified
parameter types are accepted. A bare generic name resolves when one arity exists; several arities
give error=Ambiguous with one candidate per arity, and a non-generic of the same name wins. Arity
spellings are interchangeable ('Box<T>', 'Box<int>', 'Box`1', 'Box<>', metadata
'Ns.Outer`1+Inner'). A local function is named as a member of the member declaring it
('Ns.T.M.local', 'Ns.T.M.outer.inner'); any segment may carry a parameter list.

## Conditional coverage

Coverage: every loaded target framework, plus derived compilations each toggling one preprocessor
symbol that is uniformly defined or undefined across the C# projects. A branch needing several
symbols toggled at once, or guarded by a symbol defined in some projects but not others, is not
analyzed.

## Failures

Failures are header-only: error=<code>, errorMessage=..., zero or more candidate= lines. Stale means a target file changed on disk since it was read: recompute from current
state and retry.

## Freshness

Results are a snapshot of a live solution: after a relevant change (any write, or the user saving a
file) re-query before relying on an earlier result.

## Paging

Narrow queries to the task. Fetch further pages only as needed; retrieve all relevant pages
before claiming complete coverage.

A tool supports paging only if its reference states SupportsPaging=Y.
For those tools, an oversized result is paged - count=<set size>, truncated=Y, nextStartItem=<k>; re-call
with startItem=<k> and the same other arguments. Each page repeats the container lines open above
its first item and reads standalone. maxSize (characters; 0 = the response budget, 80,000 by
default) bounds a page but not count=; maxResults caps the result set, and paging walks that capped
set (count= names its size); an item longer than a whole page is delivered as far as it fits,
partialItem=<n>, the rest unretrievable. Tools without paging cut oversized results after the last whole line: outputTruncated=Y with fullOutputChars=<n>; narrow the request
and re-call.

## Batching reads with multi_query

When several facts are needed before a change, batch the read-only queries in one multi_query
call: its operations run against one snapshot and cannot disagree; its continuation protocol is in
[its reference](references/multi_query.md).

## Editing and diagnostics

Preview broad edits: every write tool takes checkOnly=true and returns the changed files without
writing. Validation failures write nothing. Files are replaced atomically; if a batch fails during saving,
rollback is attempted but may fail. Writes are stale-guarded. Make all the
edits a task needs, then call get_diagnostics once at the end (it compiles and analyzes affected
projects, so it is costly; a repeat with nothing changed is near-instant). Fix a diagnostic with
apply_code_fix (its id, line and column); on error=Conflict pass the chosen candidate actionId to
apply_code_action. Keep the code's declared intent: do not hand-edit, apply_patch or delete a
declaration to make a diagnostic go away unless the user asks for that.

## Razor

documentPath accepts .razor and .cshtml; give positions in the Razor file itself, inside its C#
(@code/@functions, @{ }, expressions). Edits are computed on the generated C# and written back to
the Razor source in the file's own indentation; an edit that cannot be mapped back is
error=NotSupported, mismatched Razor text error=Conflict. Analyzers do not run on Razor-generated
code: compiler diagnostics are supported, plus CS8019/IDE0005 (which remove the unnecessary @using
on that line); the Razor compiler's own RZxxxx diagnostics are reported against the file;
_Imports.razor/_ViewImports.cshtml usings are never removed.

## Tool references

Before first use of a tool, read `references/<tool_name>.md`.
