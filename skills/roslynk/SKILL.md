---
name: roslynk
description: Use Roslynk for C#/.NET code review, investigation, compiler diagnostics, symbol navigation, and semantic edits when its MCP server is available. Applies to .cs, .razor, and .cshtml code — checking compilation, finding definitions, references, callers or implementations, renaming, applying fixes or refactorings, and dead-code analysis — even when the user never names Roslynk. Prefer it over grep, file reads, hand edits, and dotnet build for those questions.
---

# Roslynk

Roslynk gives semantic intelligence over the C# compiled in a loaded .NET solution: compiler and
analyzer diagnostics, symbol navigation, references, call graphs, semantic rename and refactoring,
dead-code analysis. For questions about .cs, .razor or .cshtml code in the loaded solution - does it
compile, where is this used, what does this bind to, rename it safely - use Roslynk rather than
reading files, grepping or running a build: it answers from the compiler's symbol model, so partial
classes, overloads, generated code and #if branches are handled correctly. Use dotnet test when
runtime behavior needs validation.

**How to use this skill.** The sections below are the shared guidance every tool relies on: load
them once per session, not per tool. Before using a tool for the first time, read its reference
file — the index links one per tool (`references/<tool>.md`). Do not load all references, and do
not print tool definitions; the MCP `tools/list` response already carries each tool's schema and
parameter descriptions.

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

Failures are header-only: error=<code>, errorMessage=..., zero or more candidate= lines; nothing
was written. Stale means a target file changed on disk since it was read: recompute from current
state and retry.

## Freshness

Results are a snapshot of a live solution: after a relevant change (any write, or the user saving a
file) re-query before relying on an earlier result.

## Paging

An oversized result is paged, never cut - count=<set size>, truncated=Y, nextStartItem=<k>; re-call
with startItem=<k> and the same other arguments. Each page repeats the container lines open above
its first item and reads standalone. maxSize (characters; 0 = the response budget, 80,000 by
default) bounds a page but not count=; maxResults caps the result set, and paging walks that capped
set (count= names its size); an item longer than a whole page is delivered as far as it fits,
partialItem=<n>, the rest unretrievable. get_symbol_body's startItem instead counts lines of the
returned text, and find_dead_code's maxResults caps the scan itself. The tools that cannot page
(get_symbol, find_definition, get_expression_info, get_solution_status) cut an oversized
result after its last whole line: outputTruncated=Y with fullOutputChars=<n>; narrow the request
and re-call.

## Batching reads with multi_query

When several facts are needed before a change, batch the read-only queries in one multi_query
call: its operations run against one snapshot and cannot disagree; its continuation protocol is in
[its reference](references/multi_query.md).

## Editing and diagnostics

Preview broad edits: every write tool takes checkOnly=true and returns the changed files without
writing. Writes are atomic and stale-guarded; nothing is written on any failure. Make all the
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

## Tool index

Every tool's complete contract — parameters, result shape, errors, limitations and examples — is
in its reference file under `references/`. Read the relevant reference before first use of that
tool. Write tools change the solution; all others are read-only.

Loading and status:

| Tool | Purpose |
| --- | --- |
| [open_solution](references/open_solution.md) | Load a .sln or .slnx; returns the solutionId every other tool needs. |
| [get_solution_status](references/get_solution_status.md) | Loaded solutions, per-solution status and load diagnostics. |
| [reload_solution](references/reload_solution.md) | Force a fresh from-disk reload; the manual backstop, not routine use. |

Symbols and navigation (read-only):

| Tool | Purpose |
| --- | --- |
| [get_symbol](references/get_symbol.md) | A symbol's declaration without its body, by fully-qualified name. |
| [get_symbol_body](references/get_symbol_body.md) | The verbatim source text of a declaration; pages by lines, lists partial-type parts. |
| [get_members](references/get_members.md) | A type's members grouped by declaring file, local functions nested. |
| [get_type_hierarchy](references/get_type_hierarchy.md) | Base-type chain, interfaces and derived types. |
| [find_definition](references/find_definition.md) | Go-to-definition from a source position. |
| [get_expression_info](references/get_expression_info.md) | What the compiler says about the expression at a position. |
| [search_symbols](references/search_symbols.md) | Search source-declared symbols by name substring. |

References and call graphs (read-only):

| Tool | Purpose |
| --- | --- |
| [find_references](references/find_references.md) | Every reference to a symbol, resolved semantically, not by text. |
| [find_reads](references/find_reads.md) | Reads of a field, property or parameter, with access kinds. |
| [find_writes](references/find_writes.md) | Writes of a field, property or parameter, with access kinds. |
| [get_callers](references/get_callers.md) | Methods that call a method, compiler-inserted calls included. |
| [get_callees](references/get_callees.md) | What a member's code calls, including external assemblies. |
| [find_implementations](references/find_implementations.md) | Implementations or overrides of an interface or abstract member. |

Diagnostics and dead code (read-only):

| Tool | Purpose |
| --- | --- |
| [get_diagnostics](references/get_diagnostics.md) | Compiler and analyzer diagnostics, with filters and summaryOnly. |
| [find_dead_code](references/find_dead_code.md) | Symbols with no references, conservatively filtered. |
| [find_dead_conditionals](references/find_dead_conditionals.md) | #if/#elif/#else branches never compiled in any configuration. |

Code actions and refactorings (write):

| Tool | Purpose |
| --- | --- |
| [get_code_actions](references/get_code_actions.md) | List fixes and refactorings at a position; returns actionIds. |
| [apply_code_action](references/apply_code_action.md) | Apply an actionId from get_code_actions. |
| [apply_code_fix](references/apply_code_fix.md) | Apply a diagnostic's fix by id and position. |
| [rename_symbol](references/rename_symbol.md) | Rename a symbol and all its references, Razor-aware and #if-safe. |
| [rename_parameter](references/rename_parameter.md) | Rename one parameter across its override/interface group. |
| [change_signature](references/change_signature.md) | Add an optional parameter and thread it through call sites. |
| [extract_method](references/extract_method.md) | Extract a selection into a new method or local function. |
| [remove_unused_usings](references/remove_unused_usings.md) | Remove unnecessary using and @using directives. |

Patching and batching:

| Tool | Purpose |
| --- | --- |
| [apply_patch](references/apply_patch.md) | Apply a git unified diff to text files, anchored by content. |
| [multi_query](references/multi_query.md) | Batch several read-only queries against one snapshot. |
