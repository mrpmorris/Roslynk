---
name: roslynk
description: How to use the Roslynk MCP tools (mcp__roslynk__*) effectively for C# work. Use this whenever a Roslynk server is connected and the task touches C# code in a solution — checking for compile errors or warnings, finding a symbol's definition, references, callers, or implementations, renaming symbols, applying code fixes or refactorings, finding dead code, or editing .cs files — even if the user never says "Roslynk". Consult it before reaching for grep, file reads, hand-written edits, or `dotnet build` on C# code: Roslynk's semantic tools are faster and more correct for all of those jobs.
---

# Using Roslynk

Roslynk is an MCP server that holds a live Roslyn compilation of a C# solution. Every question you'd normally answer with grep, file reads, or `dotnet build` — "where is this used?", "does it compile?", "rename this safely" — it answers from the compiler's symbol model instead. That matters because text search lies: it hits comments, strings, and unrelated same-named members, and it misses partial classes, generated code, and `#if` branches. Roslynk doesn't.

**Default to Roslynk for C# semantic work.** Fall back to plain file tools only for what Roslynk doesn't cover: non-C# files, file creation/deletion, and reading a method body once you know its location.

## Getting started

1. Call `open_solution` with the absolute path to the `.sln`/`.slnx`. It returns immediately and loads in the background; the `solutionId` it returns (which is just the solution path) is the handle every other tool needs.
2. If a call returns `error=Indexing`, the load hasn't finished. Retry the same call shortly, or poll `get_solution_status` (~1s interval) and report loading progress. **Do not fall back to reading or editing files directly** — loading finishes within seconds to a minute.
3. `open_solution` is idempotent and the daemon keeps solutions warm across sessions, so calling it again is cheap and safe.

Never call `reload_solution` on your own initiative. File changes — including edits made by you, the user, or other tools — are picked up automatically by Roslynk's file watcher. If something looks stale (e.g. after a branch switch, `dotnet restore`, or an SDK/props change), *suggest* the user run reload; only call it when they explicitly say so.

## Choosing the right tool

| You want to... | Use | Not |
|---|---|---|
| Check whether the code compiles / see warnings | `get_diagnostics` | `dotnet build` (minutes vs. instant) |
| Find where a symbol is used | `find_references` | grep (false hits in strings/comments) |
| Find who calls a method | `get_callers` | grep |
| Jump from a usage to its declaration | `find_definition` (file + line + column) | scrolling files |
| Find implementations of an interface/abstract member | `find_implementations` | grep |
| See a type's members and where they're declared | `get_members` | reading the whole file |
| Identify what a name refers to / get its signature | `get_symbol` | reading files |
| Explore base types / derived types | `get_type_hierarchy` | manual tracing |
| Find a symbol by partial name | `search_symbols` | grep across the repo |
| Rename a symbol everywhere (incl. `.razor`) | `rename_symbol` | find-and-replace |
| Apply a compiler-suggested fix | `apply_code_fix` / `get_code_actions` → `apply_code_action` | hand-editing |
| Edit .cs source directly | `apply_patch` (unified diff) | host editor Write/Edit |
| Remove unused `using` directives | `remove_unused_usings` | hand-editing |
| Add an optional parameter to a method | `change_signature` | hand-editing call sites |
| Find unused members / dead `#if` branches | `find_dead_code` / `find_dead_conditionals` | eyeballing |

To read a method's *body*, use `get_members` or `get_symbol` to get the file path and line span, then read just those lines with your normal file-read tool. Roslynk locates; the host reads.

## Addressing symbols

Most tools take a **fully-qualified name**: `Namespace.Type` or `Namespace.Type.Member` (no `global::` prefix). Names also resolve against referenced assemblies, so `System.String.Substring` works.

The intended loop when a name doesn't resolve cleanly:

- `error=Ambiguous` → the response lists `candidate=` lines with exact FQNs. Pick the right one and retry.
- `error=NotFound` → `candidate=` lines carry fuzzy suggestions. If none fit, try `search_symbols` with a substring.

Two tools are position-based instead (file path + 1-based line and column): `find_definition` (you have a cursor location, not a name) and `get_code_actions` (actions are inherently positional).

## Reading results

Every tool returns a compact text outline, not JSON: `key=value` header lines, then a blank line, then a tab-indented body (project → folders → file → namespace → type → member, with `kind,name,line:col` leaves). Booleans are `Y`/`N`. A `status=` header appears only when the solution isn't Ready — its absence means Ready.

Errors are header-only: `error=<code>` plus `errorMessage=`. Codes you'll act on: `Indexing` (retry same call), `Ambiguous`/`NotFound` (use the `candidate=` lines), `Stale` (re-read the file, recompute your edit), `Conflict` (re-run the discovery step, e.g. `get_code_actions`), `NotSupported`, `Invalid`.

Watch for `truncated=Y`: results were capped (`find_references` defaults to 100, `search_symbols` 50, `find_dead_code` 50). Raise `maxResults` or narrow the query if you need everything.

**Results are snapshots.** The solution is edited live, so re-query rather than reusing an earlier response — especially after any write.

**Leave defaulted parameters alone** unless you specifically need the non-default behavior. One that surprises people: `get_diagnostics`' include flags all default to *false*, but the header always reports `errors=/warnings=/infos=/hidden=` counts — so a bare call is a cheap "does it compile?" check, and you opt into detail (`includeErrors=true`, ...) only when counts are non-zero.

## Editing code

All write tools (`apply_patch`, `rename_symbol`, `apply_code_action`, `apply_code_fix`, `remove_unused_usings`, `change_signature`) share these behaviors:

- **`checkOnly=true` previews** the changed-file list without writing. Use it when a change might be broad (a rename of a widely-used symbol) or when you want to confirm scope before committing.
- Writes are **atomic and stale-guarded**: if a target file changed on disk since Roslynk read it, the whole batch is rejected with `error=Stale` rather than clobbering the concurrent edit. On `Stale`, just recompute from current state and retry.
- Writes go straight to disk and the in-memory model advances with them, so a `get_diagnostics` immediately after a write reflects the change — no reload, no rebuild step.

Prefer `apply_patch` over the host editor's Write/Edit for `.cs` files that are compiled in the solution — it keeps Roslynk's model in sync and gets you the stale-write protection. Its rules:

- Standard git unified diff, but hunks are **content-anchored, not line-number-anchored** — include enough surrounding context that each hunk matches exactly one place, or you'll get `error=Conflict`.
- It edits **existing solution-compiled .cs files only**. File creation, deletion, and non-.cs files are rejected (`error=NotSupported`) — use the host editor for those.

### The diagnostics loop

The core edit cycle: make a change (via any write tool) → `get_diagnostics` (bare call, read the counts) → if errors appeared, `includeErrors=true` to see them → fix → repeat. This replaces `dotnet build` entirely during development; results are effectively instant because the compilation is already in memory. Pass `includeAnalyzers=false` for an even faster compiler-only pass when you don't care about style rules yet.

### Fixing diagnostics

Two paths:

- **Quick path** — you already know the diagnostic ID (from `get_diagnostics`): `apply_code_fix(documentPath, diagnosticId)` fixes the first occurrence of that ID in the file. One call, no handle juggling.
- **Full path** — you want to see what's on offer at a location: `get_code_actions(documentPath, line, column)` lists fixes and refactorings with opaque `actionId`s; pass one verbatim to `apply_code_action`. Action IDs aren't cached server-side — if the code changed in between, you'll get `error=Conflict`; re-run `get_code_actions` and pick again.

### Rename and signature changes

`rename_symbol` is compiler-correct across partial classes, all `#if` branches, every target framework, and **Razor**: usages in `.razor`/`.cshtml` markup and `@code` blocks are rewritten in the actual Razor source. Trust it over any textual replace. The new name must be a valid C# identifier.

`change_signature` (v1) does exactly one thing: append a single *optional* parameter to an ordinary method, optionally threading an argument into every call site. It refuses virtual/override/abstract/interface/partial methods, overloaded methods, constructors, and operators — for those, fall back to `apply_patch` plus `find_references` to update call sites yourself.

### Dead-code cleanup

`find_dead_code` reports candidates with `High`/`Medium` confidence and a reason — it never deletes. It already excludes interface implementations, overrides, test methods, generated code, and DI-attributed members, but treat results as *candidates*: public API may be used externally, reflection can hide uses. Confirm intent with the user before bulk-deleting. The reported `loc` is the full declaration span, ready to hand to `apply_patch` for removal. On large solutions pass `scope` (an FQN prefix) — the scan is per-symbol and whole-solution scans are slow. `find_dead_conditionals` similarly flags `#if` branches never compiled under any loaded configuration, with the caveat that configurations Roslynk hasn't loaded (CI-only defines, missing workloads) can produce false positives.

## Limits to remember

- Roslynk covers **.cs files compiled in the loaded solution** (plus Razor via its generated code, read-mostly — only `rename_symbol` writes back to `.razor`/`.cshtml`). Everything else — csproj, json, md, new files — is the host's job.
- `search_symbols` searches source-declared symbols only, not referenced assemblies; `get_symbol`/FQN resolution *does* reach metadata.
- Multi-targeted projects: pin `get_diagnostics` with `targetFramework` if you need one TFM's view.

For exact parameter lists, output shapes, and per-tool gotchas, read [references/tools.md](references/tools.md).
