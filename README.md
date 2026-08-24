# Roslynk

**Roslyn + link** — the link between AI test harnesses and Roslyn.

Add the MCP to your AI harness, then type "Open (solution file name)" - that's it!

The biggest time saver you will see is checking for compiler errors and warnings; with
Roslynk it is practically instant, no need to wait minutes for the solution to build.

Roslynk gives an MCP client (e.g. Claude) semantic intelligence over C# code via Roslyn:
* diagnostics
* symbol navigation
* find-references
* semantic rename
* code actions
* dead-code detection
* and more

All operating directly on the projects compiled in a loaded solution!

- **Transport:** HTTP only, bound to loopback (`127.0.0.1`/`::1`).
- **Host:** foreground console on Linux/WSL/macOS; on Windows, also installable as a headless service. No UI.
- **Observability:** OpenTelemetry, exported via OTLP to a backend you configure.


## Connect an MCP client (all platforms)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on `PATH`. The `stdio` verb is a
self-launching bridge: the MCP client spawns it, it starts the shared HTTP daemon on
`localhost:6502` if one isn't already running, and pipes the session through. Nothing to launch or
babysit by hand.

First install the global .NET tool from NuGet (no clone, no build needed):

```bash
dotnet tool install roslynk -g
```

### Claude Desktop

```bash
claude mcp add roslynk -- dnx Roslynk --yes -- stdio
```

### Manual `mcp.json` (VS Code, Cursor, Windsurf, and others)

Add the following entry to your MCP configuration file (e.g. `.vscode/mcp.json`,
`~/.cursor/mcp.json`, or your AI tool's equivalent):

```json
{
  "mcpServers": {
    "roslynk": {
      "type": "stdio",
      "command": "dnx",
      "args": ["Roslynk", "--yes", "--", "stdio"]
    }
  }
}
```

> **Important:** the `stdio` argument at the end is required. Without it the process starts as an
> HTTP daemon and never responds to the MCP `initialize` handshake, causing the client to hang.

Tagged releases (e.g. `1.0.0-beta.1`, no `v` prefix) are packed and pushed to nuget.org by CI.

From a source checkout:

```bash
claude mcp add roslynk -- dotnet run --project /path/to/Roslynk/Source/App/Morris.Roslynk.Mcp -- stdio
```

Or in `mcp.json` from a source checkout:

```json
{
  "mcpServers": {
    "roslynk": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/Roslynk/Source/App/Morris.Roslynk.Mcp", "--", "stdio"]
    }
  }
}
```

(With a published build, use `Morris.Roslynk.Mcp stdio` as the command instead — it starts faster.)

The daemon outlives individual sessions so Roslyn workspaces stay warm across clients. Its console
output goes to `Roslynk/daemon.log` under the local application-data folder (`~/.local/share` on
Linux, `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on Windows).

## 🤖 Configure your agent

Connecting the MCP server is only half the job. Left to its own habits an agent will still `grep`
for call sites, read whole files to find a member, and run `dotnet build` to check for errors — all
slower and less correct than the equivalent Roslynk call. Teach it the tools once and it stops.

### Claude Code: install the skill

This repo ships a ready-made skill in [`skills/roslynk`](skills/roslynk). Copy it into your skills
folder and Claude will load it automatically whenever a task touches C#:

```bash
# user-wide (all projects)
cp -r skills/roslynk ~/.claude/skills/

# or per-project
cp -r skills/roslynk .claude/skills/
```

### Everything else: example system prompt for LLMs

> [!IMPORTANT]
> **⚡ Pro Tip:** Add this system prompt to your `AGENTS.md` / `CLAUDE.md`, Cursor Rules, Windsurf
> rules, or any AI agent configuration file. It dramatically improves how often and how well your
> LLM reaches for Roslynk instead of grepping, reading files, or rebuilding the solution.

> You have access to `roslynk`, an MCP server holding a live Roslyn compilation of a C# solution.
> Prefer it over grep, file reads, hand-written edits, and `dotnet build` for anything semantic:
> it answers from the compiler's symbol model, so it sees partial classes, generated code, inactive
> `#if` branches and Razor, and it never matches names inside comments or strings.
>
> **Getting started:** call `open_solution` with the absolute path to the `.sln`/`.slnx`; the
> `solutionId` it returns is the handle every other tool needs. It loads in the background — while
> it does, other calls return `error=Indexing`; retry shortly or poll `get_solution_status`. Never
> fall back to reading or editing files directly. Never call `reload_solution` on your own
> initiative — a file watcher picks up edits automatically; suggest a reload instead.
>
> **Lifecycle:**
> - `open_solution`: Load a solution. Parameters: `solutionPath` (absolute). Idempotent, cheap to repeat.
> - `get_solution_status`: List every loaded solution and its progress. No parameters.
> - `reload_solution`: Force a from-disk re-evaluation. Parameter: `solutionId`. Only when the user explicitly asks.
>
> **Navigation:**
> - `find_definition`: Go to definition from a cursor position. Parameters: `solutionId`, `filePath`, `line`, `column` (1-based).
> - `get_symbol`: Identify a symbol and get its declaration. Parameters: `solutionId`, `symbolName` (fully-qualified).
> - `get_members`: List a type's members with their locations. Parameters: `solutionId`, `typeName`, `includeInherited`, `nameFilter`, `includeMethods`/`includeFields`/`includeProperties`/`includeEvents`/`includeNestedTypes`.
> - `search_symbols`: Find symbols by partial name. Parameters: `solutionId`, `query`, `maxResults`.
>
> **Relationships:**
> - `find_references`: Every usage of a symbol. Parameters: `solutionId`, `symbolName`, `maxResults`.
> - `get_callers`: Who calls a method, overloads resolved. Parameters: `solutionId`, `methodName`.
> - `find_implementations`: Implementors/overrides of an interface, abstract or virtual member. Parameters: `solutionId`, `symbolName`.
> - `get_type_hierarchy`: Base types, interfaces and derived types. Parameters: `solutionId`, `typeName`.
>
> **Diagnostics:**
> - `get_diagnostics`: Compile check — this replaces `dotnet build`. Parameters: `solutionId`, `includeErrors`, `includeWarnings`, `includeInfo`, `includeHidden` (**all default false**), `includeAnalyzers`. The header always reports `errors=`/`warnings=`/`infos=`/`hidden=` counts, so a bare call is a cheap "does it compile?"; opt into detail only when the counts are non-zero.
>
> **Code actions:**
> - `get_code_actions`: List fixes and refactorings at a position. Parameters: `solutionId`, `documentPath`, `line`, `column`, `endLine`, `endColumn`.
> - `apply_code_action`: Apply one by its opaque `actionId` (pass it back verbatim). Parameters: `solutionId`, `actionId`, `checkOnly`.
> - `apply_code_fix`: Fix the first occurrence of a diagnostic id in a file, no round-trip. Parameters: `solutionId`, `documentPath`, `diagnosticId`, `checkOnly`.
>
> **Editing:**
> - `apply_patch`: Edit text files with a git unified diff. Parameters: `solutionId`, `patch`, `baseVersions`, `checkOnly`. Hunks are content-anchored, not line-number-anchored — include enough context that each matches exactly one place.
> - `rename_symbol`: Compiler-correct rename across partial classes, every `#if` branch, every target framework, and `.razor`/`.cshtml`. Parameters: `solutionId`, `symbolName`, `newName`, `checkOnly`.
> - `change_signature`: Append one optional parameter to an ordinary method and thread an argument into every call site. Parameters: `solutionId`, `methodId`, `parameterType`, `parameterName`, `defaultValue`, `callSiteArgument`, `checkOnly`.
> - `remove_unused_usings`: Strip unused `using` directives (CS8019). Parameters: `solutionId`, `documentPath` (omit for the whole solution), `checkOnly`.
>
> **Dead code:**
> - `find_dead_code`: Unreferenced members with a confidence and a reason — candidates, never verdicts; it deletes nothing. Parameters: `solutionId`, `scope` (FQN prefix; use it on large solutions), `includePublic`, `maxResults`.
> - `find_dead_conditionals`: `#if` branches never compiled under any loaded configuration. Parameter: `solutionId`.
>
> **Conventions:** most tools take a fully-qualified `Namespace.Type.Member` name (no `global::`);
> `find_definition` and `get_code_actions` are position-based instead. Responses are a compact
> `key=value` header block, a blank line, then a tab-indented outline; booleans are `Y`/`N`. Errors
> are header-only: `error=Indexing` (retry), `Ambiguous`/`NotFound` (pick from the `candidate=`
> lines), `Stale` (re-read and recompute), `Conflict` (re-run the discovery step). Watch for
> `truncated=Y` and raise `maxResults`. Leave defaulted parameters alone unless you need the
> non-default behaviour, and re-query rather than reusing an earlier response — the solution is
> edited live.
>
> **Writing safely:** every write tool accepts `checkOnly=true` to preview the changed-file list
> without writing — use it before a broad rename. Writes are atomic and stale-guarded: if a file
> changed on disk since Roslynk read it the whole batch is rejected with `error=Stale` rather than
> clobbering the other edit. Successful writes advance the in-memory model immediately, so
> `get_diagnostics` straight afterwards reflects the change — no reload, no rebuild.
>
> **The edit loop:** make a change → `get_diagnostics` (bare call, read the counts) → if errors
> appeared, re-call with `includeErrors=true` → fix with `apply_code_fix` or `apply_patch` → repeat.

## Run the daemon manually (Linux / WSL / macOS)

For a foreground daemon with visible logs:

```bash
./installer/run.sh
```

Listens on `http://localhost:6502`. Point your MCP client at that URL (streamable HTTP). Ctrl+C stops it.

## Run (Windows)

Foreground dev:

```powershell
dotnet run --project Source/App/Morris.Roslynk.Mcp
```
