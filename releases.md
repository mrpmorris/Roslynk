# Releases

# Unreleased

- `rename_symbol` no longer overwrites edits made while it was computing: only the documents it changed are replayed onto the latest model, edits to other files are kept, and a changed file it touches, or a file changed on disk, returns `error=Stale` with a `stale=` path instead of `error=Faulted`. The tool can now be cancelled (Fixes #54)
- **Breaking:** `apply_code_fix` now requires `line` and `column` (1-based, as `get_diagnostics` prints them) and fixes the diagnostic at that position, instead of the first occurrence of the id in the file. No diagnostic with that id at the position → `error=NotFound`. In `.razor`/`.cshtml` the position is in the Razor file; `CS8019`/`IDE0005` remove the unnecessary `@using` on that line (Fixes #53)
- `apply_code_fix` no longer picks one fix arbitrarily when a diagnostic has several (e.g. `CS0535` implement vs implement explicitly, `CS0246` add using vs generate type): it writes nothing and returns `error=Conflict` with a `candidate=<actionId>,Fix,<diagnosticId> <title>` header per distinct fix, to be applied with `apply_code_action` (Fixes #53)
- `get_code_actions` lists the individual fixes and refactorings inside a grouped action (such as the choices under a "Generate type" group) instead of the group itself, which produced no changes when applied
- New `find_reads` and `find_writes` tools: semantic reads/writes of a field, property or parameter (addressed as `Namespace.Type.Member:parameter`), each location tagged `read`, `assign`, `compound`, `increment`, `ref`, `out` or `init`. Dual read/write accesses (`compound`, `increment`, `ref`) appear in both tools. Output follows `find_references` with one location per leaf. Available in `multi_query` (Fixes #27)
- New `get_expression_info` tool: position-based compiler facts about an expression in a `.cs`, `.razor` or `.cshtml` file — its type and converted type, the symbol it binds to (with the overload selected, generic/extension instantiation, and candidates when unresolved), nullable annotation and flow state, constant value, implicit conversion kind, source/metadata origin and XML-doc summary. Unavailable facts are reported as `none`; a position on a declared name (local, parameter, field, member, type) reports the declared symbol and its type like an editor hover; a keyword, punctuation, comment or whitespace is `error=NotFound`. Available in `multi_query` (Fixes #26)
- New `rename_parameter` tool: rename one parameter of a method, constructor or indexer overload via Roslyn rename, updating named arguments and `<paramref>` docs and cascading across override/interface groups; reports related declarations left unchanged, and refuses on name conflicts (Fixes #44)
- Fully-qualified names now resolve explicitly declared constructors, written `Namespace.Type.Type(...)`
- `rename_symbol` reports overlapping edits as `error=Conflict` instead of faulting
- New `extract_method` tool: extract a selection of statements or an expression into a method or local function via Roslyn's Extract Method refactoring, with optional naming and preview; refuses without writing when Roslyn cannot extract the selection, flags a behavior change, or the result would add compile errors (Fixes #46)
- `change_signature` supports methods declared in `.razor`/`.cshtml` `@code` blocks and call sites in Razor markup: edits are mapped back through `#line` directives and written to the Razor source. Previously they were reported as applied but never written to disk. Unmappable edits are refused without writing, and stale files return `error=Stale`
- `get_code_actions`, `apply_code_action`, `apply_code_fix`, `extract_method` and `remove_unused_usings` work on `.razor` and `.cshtml` files: positions are given in the Razor file, Roslyn runs on the generated C#, and the result is written back to the Razor source in the file's own indentation. Edits to generated scaffolding with no Razor source are refused (`error=NotSupported`) with nothing written
- `remove_unused_usings` (and `apply_code_fix` with `CS8019`/`IDE0005`) removes unnecessary `@using` lines from `.razor`/`.cshtml` files, including in a whole-solution run; `_Imports.razor`/`_ViewImports.cshtml` are never edited because other files share them
- `extract_method` in a `.cshtml` view where Roslyn cannot add a new method suggests `asLocalFunction=true`
- `apply_code_action`, `apply_code_fix` and `remove_unused_usings` report a file edited on disk since load as `error=Stale` instead of faulting
- Local functions can be named: `Namespace.Type.Method.local`, or `Namespace.Type.Method.outer.inner` for one declared inside another, through any nesting of classes. Any segment may carry a parameter list to pick an overload (`Namespace.Type.Method(int).local`). Supported by every name-based tool (`get_symbol`, `get_symbol_body`, `find_references`, `get_callers`, `rename_symbol`, `rename_parameter`, `change_signature`, and the same operations in `multi_query`). Tools emit the fully signed form, and `Ambiguous` candidates for overloaded containers round-trip
- `rename_parameter` and `change_signature` accept local functions
- `search_symbols` finds local functions; outlines report them as kind `localfunction`, nested under their containing member, in `search_symbols`, `get_callers` and `find_references`, and `find_definition` returns their full name
- `extract_method` reports the extracted method's full name in a new `symbolName` header
- `get_members` lists each member's local functions nested beneath it, including local functions declared inside other local functions
- Write pipeline can guard a precomputed update against an intervening edit to the same documents, without reverting unrelated intervening edits

## 1.2.0

- Steer impact/usage questions to multi_query: impact-analysis recipe in the roslynk skill plus multi_query/find_references description hints and schema tests (Fixes #22)
- Multi query: batch multiple read-only tool calls in one request (Fixes #31)
- Ambiguous requests: clarify before acting (Fixes #34)
- New `get_symbol_body` tool (Fixes #35)
- Fix analyzer diagnostics through the code-fix path (Fixes #9)
- Defaulted tool parameters genuinely omittable (Fixes #30)
- Docs: DeepSeek harness instructions, agent configuration section, manual mcp.json setup for non-Claude AI tools

## 1.1.0
- Allow apply_patch to work on any solution file

## 1.0.0

- Don't lock analyzer DLLs (Closes #1)
- New logos
- Add roslynk skill teaching AI agents effective use of the MCP tools
- Stability fixes

## 1.0.0-beta.2

- Fix NullReferenceException
- Remove multi-target framework handling
- Updated instructions

## 1.0.0-beta.1

Initial public beta — full MCP server over Roslyn, cross-platform stdio bridge + dotnet tool (dnx/NuGet), replacing the Windows installer.

- Tools: open/close_solution, get_solution_status, reload_solution, clear_cache, build_solution; get_symbol, find_references, find_definition, find_implementations, get_type_hierarchy, get_members, rename_symbol, change_signature, apply_patch, apply_code_action/apply_code_fix, get_code_actions, get_diagnostics, find_dead_code, remove_unused_usings, get_method
- Fuzzy resolution with ranked candidates on ambiguous get_symbol
- Metadata reads from referenced assemblies
- Pagination + near-miss suggestions on find_references
- Razor support (*.razor, *.razor.g.cs, additional files, generated-file tracking)
- Run analyzers and code generators; incremental diagnostics
- Atomic, hash-checked writes; file watcher resync (excludes obj/bin)
- Result envelope for all tools; background solution loading; idle eviction
- Shadow-copied binaries, faster startup, optional error details, OTEL tracing
