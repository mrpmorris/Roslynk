# Roslynk tool reference

Detailed per-tool reference. Read the section you need; SKILL.md carries the workflows.

## Contents

- [Shared conventions](#shared-conventions)
- [Lifecycle: open_solution, get_solution_status, reload_solution](#lifecycle)
- [Navigation: find_definition, get_symbol, get_symbol_body, get_members, search_symbols](#navigation)
- [Relationships: find_references, get_callers, get_callees, find_implementations, get_type_hierarchy](#relationships)
- [Diagnostics: get_diagnostics](#diagnostics)
- [Code actions: get_code_actions, apply_code_action, apply_code_fix](#code-actions)
- [Editing: apply_patch, rename_symbol, change_signature, remove_unused_usings](#editing)
- [Dead code: find_dead_code, find_dead_conditionals](#dead-code)
- [Batching: multi_query](#batching)

## Shared conventions

**Output format.** Compact text outline: `key=value` header lines, a blank line, then a tab-indented body. No blank line means the response is all headers (typically an error). Booleans render `Y`/`N`. Newlines inside a field are literal `\n`. A `status=` header (`Building`|`Faulted`) appears only when the solution is not Ready.

**Body nesting** (multi-symbol tools): project → one line per folder segment → file → namespace → `typeKind,typeName,loc` → `memberKind,memberName,loc`. `kind` ∈ `method|property|field|event|class|struct|interface|enum|delegate`. `loc` is `line:col` or `startLine:startCol-endLine:endCol`, 1-based; multiple locations on one line are pipe-delimited. Names containing commas (e.g. `Dictionary<string, int>`) are single-quoted.

**Error shape** (header-only):

```
error=<Indexing|Faulted|NotFound|Ambiguous|NotSupported|Stale|Invalid|Conflict|Truncated>
errorMessage=<text>
candidate=<name>       (0+ lines: NotFound suggestions / Ambiguous matches)
stale=<path>           (0+ lines, Stale only)
```

**Symbol names.** A name is a fully-qualified name, optionally carrying a parameter-type list for a
method or indexer: `N.T.M`, `N.T.M(int, string)`, `N.T.this[int]`, `N.T.M<T>(T)`. Parameter names,
default values, nullable annotations and `global::` are ignored on input, and fully-qualified parameter
types are accepted, so `M(System.Int32)` and `M(int value = 0)` both resolve `M(int)`. A `ref`/`out`/`in`
modifier is optional but honoured when written. Without a parameter list the name matches every overload
→ `error=Ambiguous`, one `candidate=` per match; every candidate is accepted verbatim by the tool that
emitted it, and the `resolvedSymbol`/`resolvedType`/`resolvedMethod`/`#fullName` a tool echoes is in the
same re-queryable form.

**Truncation.** Known-total tools emit `count=<total>` and `truncated=Y` when capped; unknown-total scans (`find_dead_code`) emit only `truncated=Y`.

**Defaults.** Don't pass a value for a defaulted parameter unless you need the non-default behavior.

**`#if` projections.** Symbol tools (find_definition, find_implementations, get_members, get_symbol, get_symbol_body, get_type_hierarchy, find_references, get_callers, get_callees, rename_symbol, search_symbols) also cover inactive `#if`/`#else` branches: Roslynk builds derived compilations toggling each uniformly-defined preprocessor symbol, and unions/dedupes results.

## Lifecycle

### open_solution
`solutionPath` (required): absolute path to `.sln`/`.slnx`.
Returns `solutionId` (= the path you passed), `status`, `projects`, `loadDiagnostics`, then one `<projectPath>,<documentCount>` line per project. Loads in the background; idempotent; missing file → `NotFound`, failed load → `Faulted`. While loading, other tools return `error=Indexing` — retry them shortly; never fall back to raw file edits.

### get_solution_status
No parameters. Lists every solution loaded by the daemon (daemon-wide, not session-scoped): `<solutionId>,<status>,<loaded>/<total>` per line. `total` is `?` until the first load completes. Status values: `Building`, `Updating`, `Ready`, `Faulted`.

### reload_solution
`solutionId` (required). Forces a from-disk MSBuild re-evaluation; the old snapshot keeps serving as `Building` until the fresh one is ready. Manual backstop for missed watcher events (network/WSL filesystems), `dotnet restore`, SDK/`global.json`/`Directory.Build.props` changes, branch switches, or retrying a `Faulted` load. **Never call it unless the user explicitly instructs you to** — suggest it instead.

## Navigation

### find_definition
`solutionId`, `filePath` (absolute or solution-relative), `line`, `column` (1-based). Position-based go-to-definition. Returns `#fullName`, `#kind`, plus `#project`/`#path`/`#loc` for source symbols or `#assembly=` for metadata-only. (Note the `#`-prefixed header style, unique to this tool family.)

### get_symbol
`solutionId`, `symbolName` (FQN, any symbol kind including members). Unambiguous source match → `#project`/`#path`/`#loc` headers + body containing the verbatim declaration text cut before the body (brace/`=>` excluded). Metadata symbol → `#source=metadata`, `#kind`, `#signature`, `#assembly`. Ambiguous → `error=Ambiguous` with one `candidate=` per match. Preferred over reading a file to identify a symbol.

### get_symbol_body
`solutionId`, `symbolName` (FQN, any declared symbol kind), `includeLeadingTrivia` (default false). Returns the symbol's **complete source text including its body**, verbatim — original indentation, line endings and spacing, no reformatting or truncation. Headers `#project`/`#path`/`#loc`, a blank line, then the text. `includeLeadingTrivia=true` extends the text (and the `loc`) back over the declaration's comments, XML docs and directives. A partial type or partial method declared in several files returns `#parts=<n>` and one `part=<n>,project=...,path=...,loc=...` line per block instead. Overloads (or any name matching several distinct symbols) — `error=Ambiguous`, resolved by retrying with a candidate's parameter-type list; a metadata/referenced-assembly symbol — `error=NotSupported` (use `get_symbol` for its signature). Prefer this over reading or grepping the file to see what a member does.

### get_members
`solutionId`, `typeName` (FQN), `includeInherited` (default false), `nameFilter` (default null; trailing `*` = prefix match, otherwise case-insensitive substring), `includeMethods`/`includeFields`/`includeProperties`/`includeEvents`/`includeNestedTypes` (all default true). Lists all members including private, grouped by declaring file (`<metadata>` bucket for referenced-assembly types). Member lines: `kind,name,loc[,paramType|paramType|...]` (param list only for methods with parameters). Returns declarations + spans, not bodies — use `get_symbol_body` for a member's body.

### search_symbols
`solutionId`, `query` (case-insensitive substring), `maxResults` (default 50). Source-declared symbols only (no metadata). Matched members nest under their type; a type's `loc` appears only if the type itself matched.

## Relationships

### find_references
`solutionId`, `symbolName` (FQN), `maxResults` (default 100). Compiler-accurate: skips comments, strings, unrelated same-named members. References grouped file→namespace→type→member; pipe-delimited `loc`s on one line for multiple hits. Deduped across `#if` projections. `count=`/`truncated=Y` when capped.

### get_callers
`solutionId`, `methodName` (FQN). Callers grouped file→namespace→containing type→calling member with the caller's declaration `loc`. Target one overload by writing its parameter-type list.

### get_callees
`solutionId`, `methodName` (FQN), `maxResults` (default 100). Callees grouped file→namespace→containing type→called member with the callee's declaration `loc`. Target one overload by writing its parameter-type list. Only source-declared callees are listed — a framework or metadata method (e.g. from the BCL) is not; an implicitly-synthesized constructor (a class's default constructor) is listed at the location of its declaring type. Calls inside a nested lambda, anonymous method or local function are attributed to that nested body, not to the resolved method. `count=`/`truncated=Y` when capped.

### find_implementations
`solutionId`, `symbolName` (FQN of interface, abstract member, or virtual member). Implementors/overrides across all projections, deduped.

### get_type_hierarchy
`solutionId`, `typeName` (FQN). Up to three sections — `base`, `interfaces`, `derived` (omitted when empty) — entries `typeKind,FQN`.

## Diagnostics

### get_diagnostics
`solutionId`, `includeErrors`/`includeWarnings`/`includeInfo`/`includeHidden` (**all default false**), `includeAnalyzers` (default true; `false` = faster compiler-only pass).

Header always carries `errors=`, `warnings=`, `infos=`, `hidden=` counts regardless of include flags, so filtering is never silent — a bare call is a cheap compile check. Body (per included severity) nests file→severity→`<id>,<line:col>,<message>`. Ids that exist only to trigger a code fix (they carry no message and accompany a public rule, as IDE0005's does) are not listed; fix the public id instead. Multi-targeted projects report diagnostics across their loaded target frameworks. Results are cached per `includeAnalyzers` and invalidated on any write, so repeated calls are cheap. This replaces `dotnet build` for correctness checking.

## Code actions

### get_code_actions
`solutionId`, `documentPath` (.cs, absolute or solution-relative), `line`, `column` (1-based), `endLine`/`endColumn` (optional, for a selection span). Returns lines `<actionId>,<kind>,<diagnosticId> <title>`; `kind` ∈ `Fix|Refactoring`; `diagnosticId` is `-` for refactorings. Fixes cover analyzer diagnostics as well as compiler ones, so `IDE*` and `CA*` ids appear here when the project references the analyzer that reports them. Capped at 50. `actionId` is opaque — pass it back verbatim.

### apply_code_action
`solutionId`, `actionId` (from get_code_actions), `checkOnly` (default false). The action is re-resolved at apply time, not cached — if the code changed since discovery, `error=Conflict`: re-run `get_code_actions`. Malformed id → `error=Invalid`. Returns `applied`, `action`, changed files.

### apply_code_fix
`solutionId`, `documentPath`, `diagnosticId` (a compiler id such as `CS0219`, or an analyzer id such as `IDE0005`), `checkOnly` (default false). Quick path: fixes the first occurrence of that diagnostic in the file without an actionId round-trip. No such diagnostic in the file → `NotFound`; no registered fix → `NotSupported`; fix produced no changes → `Conflict`.

Analyzer ids are only reported — and so only fixable — when the project actually references the analyzer. `IDE*` rules come from the CodeStyle analyzers, which a project pulls in with `EnforceCodeStyleInBuild` or an explicit `Microsoft.CodeAnalysis.CSharp.CodeStyle` package reference; an SDK copy built against a newer compiler than Roslynk's is skipped.

## Editing

All write tools: `checkOnly=true` previews changed files without writing; writes are atomic (all-or-nothing) and stale-guarded (on-disk content re-hashed against the loaded snapshot; mismatch → `error=Stale`, nothing written). Successful writes advance the in-memory model immediately. Response shape: `applied=Y|N` + tool-specific headers + changed-file list grouped by project.

### apply_patch
`solutionId`, `patch` (git unified diff: `---`/`+++`/`@@` hunks, one or more file sections), `baseVersions` (optional list of `{path, version}` from a prior read, for explicit optimistic concurrency), `checkOnly`.

- Hunks are **content-anchored**; line numbers in `@@` headers are untrusted and may be omitted (`@@`). Each hunk must match exactly one place — include enough context. Ambiguous or unmatched → `error=Conflict` naming the file and reason.
- Edits any existing text file inside the solution folder. Compiled `.cs` and `.razor`/`.cshtml` documents (regular and additional) are folded into the in-memory model; other files (`.csproj`, `.json`, `.md`, ...) are written to disk only. Creation, deletion, binary files, `obj`/`bin` paths, and paths outside the solution folder are rejected before any hunk is attempted: `error=NotSupported` with `rejected=<path>` lines. Encoding (UTF-8/UTF-16 BOM) is preserved.
- Path resolution: absolute → solution-relative → unique suffix match across solution documents (regular and additional); a path not in the model falls back to solution-folder-relative.
- The stale-write guard runs even without `baseVersions`.

### rename_symbol
`solutionId`, `symbolName` (FQN), `newName` (must be a valid C# identifier), `checkOnly`. Renames across partial classes, all `#if` projections, all TFMs, and Razor: edits computed against generated `.g.cs` are mapped back through `#line` directives and written to the real `.razor`/`.cshtml` (covering `@code` blocks, markup expressions, and component attributes in other components). Unverifiable mapping aborts the whole rename before writing. Ambiguous → `error=Ambiguous` with candidates; retry with one verbatim to rename a single overload.

### change_signature
`solutionId`, `methodId` (FQN), `parameterType` (e.g. `System.Threading.CancellationToken`), `parameterName` (valid identifier), `defaultValue` (**required** — keeps the parameter optional, e.g. `default`, `null`, `0`), `callSiteArgument` (optional; when given, threaded into every call site as a named argument), `checkOnly`.

v1 scope: appends one optional parameter to one ordinary method. `NotSupported` for virtual/override/abstract methods, interface members and implementations, partial methods, `params` methods, constructors/operators/accessors/local functions. An overloaded method is targeted by its parameter-type list; a bare name → `Ambiguous`. Only true invocation call sites are updated — method groups, `nameof`, and `cref` are left alone (still valid because the parameter is optional). Returns `updatedCallSites` count.

### remove_unused_usings
`solutionId`, `documentPath` (optional; omit to clean the whole solution), `checkOnly`. CS8019 finds the files to touch; each one is then rewritten by Roslyn's own IDE0005 fix, which preserves the surrounding trivia, falling back to a syntactic removal when the IDE0005 analyzer is not referenced. Nothing to remove → `applied=N`, `removedCount=0` (not an error). Safe to re-run (idempotent).

## Dead code

### find_dead_code
`solutionId`, `scope` (optional FQN prefix, e.g. `MyApp.Services` — use it on large solutions; the scan runs find-references per symbol), `includePublic` (default false — public/protected API excluded unless set), `maxResults` (default 50).

Leaf lines: `memberKind,memberName,loc,confidence,reason` with confidence `High|Medium`. Automatically excludes interface implementations, virtual/override/abstract chains, test-attributed members (xUnit/NUnit/MSTest), generated code, and DI/reflection-attributed members (`[Export]`, `[ImportingConstructor]`). The `loc` is the full declaration span, ready to pass to `apply_patch` for removal — this tool never deletes anything itself. Treat results as candidates, not verdicts.

### find_dead_conditionals
`solutionId` only. Header `#deadConditionals=<n>`; body per file: `<line:col>,<directive>,<condition>` with directive ∈ `if|elif|else` (condition `(else)` for `#else`). Flags branches never compiled under any configuration Roslynk actually loaded (each project's defined symbols, and that set minus DEBUG, across TFMs). A branch used only by a configuration not loaded (CI-injected define, missing workload) is a false positive — treat as "possibly dead".

## Batching

### multi_query
`solutionId`, `operations` (1-25 items), optional `expectSnapshot`.

Each operation is `{ "tool": <name>, "arguments": { ... } }` where `tool` is one of the 12 read-only query tools (get_symbol, get_symbol_body, get_members, find_definition, find_implementations, find_references, get_callers, get_callees, search_symbols, get_type_hierarchy, find_dead_code, find_dead_conditionals) and `arguments` uses exactly that tool's single-call parameter names - unknown or misspelled keys are rejected (`error=Invalid` naming the key), never ignored; omitted parameters take the tool's declared defaults. The schema's `tool` enum lists the legal names, so a write tool, get_diagnostics or get_solution_status is unrepresentable and fails the whole call at binding (`error=Invalid` naming the offending value and the permitted set).

Everything runs against ONE snapshot of the solution, so results inside one response can never disagree with each other. The response is one envelope, not JSON:

```
operations=<n>
snapshot=<32-hex id of the snapshot every slot was computed against>
boundary=<32-hex, fresh per request>

--<boundary>
slot=<i> tool=<tool name>

<that tool's own output format, verbatim - headers, blank line, tab-indented body, or its error= block>

--<boundary>
...

--<boundary>--
```

A failing operation does not abort the batch: its slot carries its normal `error=` block (NotFound/Ambiguous/Invalid/...) and the other slots still deliver. Slot bodies are never cut mid-output.

**Truncation and continuation.** More than 25 operations, or a total output over the response budget, truncates: unexecuted operations still get their numbered slot carrying `error=Truncated`, and the header carries `truncatedSlots=<n>`. To continue, re-send exactly the operations whose slots were Truncated - the slot meta line gives the index and tool name, but NOT the arguments, so rebuild the continuation from your own copy of the request (the server does not echo them back). Pass the first response's `snapshot=` value as `expectSnapshot` when you do: if the solution changed between the calls (another client, or the user saving a file) the continuation is refused with `error=Stale` naming both ids plus a `snapshot=<current id>` header - re-run the whole batch instead of stitching two generations. Consistency is guaranteed within one response, never across two. `get_diagnostics` and `get_solution_status` are deliberately not multi-queryable - call them directly.
