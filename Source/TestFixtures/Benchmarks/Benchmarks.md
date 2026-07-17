Benchmark the Roslynk MCP server's C# tools against standard shell tools (grep/sed/dotnet)
on two axes: warm latency (ms) and payload token cost. Produce one results table.

## Environment
- Roslynk speaks JSON-RPC over HTTP/SSE at http://127.0.0.1:6502/ (NOT via the MCP tool
  layer — call the HTTP endpoint directly with a Python urllib harness so you can time it
  and measure the decoded payload).
- Handshake: POST `initialize` (protocolVersion 2024-11-05) → read the `Mcp-Session-Id`
  response header → POST `notifications/initialized` with that header → then `tools/call`.
  Accept header must be "application/json, text/event-stream". Responses are SSE: the JSON
  is on the last line starting with "data:".
- Solution under test: C:\Data\CompanyData\SBSoftware\VendManagerWeb\Vendmanager.sln
  (9 projects). Use this exact path as the `solutionId` argument on every call.

## Setup
1. Call `open_solution` {solutionPath: <the sln path>}.
2. Poll `get_solution_status` until the line CONTAINING "Vendmanager" shows ",Ready,".
   Caveat: multiple solutions may be listed (e.g. a BlazorApp7 line that is already Ready) —
   match the Vendmanager line specifically, don't just test for "Ready" in the blob.
3. Retry any tool call whose payload contains "Indexing" (sleep 1s, up to ~30x).

## Measurement (identical for MCP and shell)
- Warm latency = median of 3 timed runs after 1 warm-up call.
- Tokens = round(len(decoded content text) / 4). Decode the SSE, concat
  result.content[*].text, count chars, divide by 4. Do NOT use wire bytes.

## Roslynk tools + scenarios (15)
Confirm parameter names with `tools/list` first. IMPORTANT: the codebase changes between
runs, so DO NOT hardcode symbol targets — resolve them against the current code first:
run `get_members {typeName: "...TaskManager", nameFilter: "Search*"}` and pick real,
currently-existing symbols; verify each scenario returns a real result (not
"error=NotFound") before recording it. Overloaded methods may fail to resolve — prefer an
unambiguous target. For find_definition, open the target .cs file and pick a file+line+column
that actually sits on an identifier usage today.

Tools to cover (arg names as of tools/list):
  search_symbols {query, maxResults}
  find_definition {filePath, line, column}
  find_references {symbolName, maxResults}
  find_implementations {symbolName}
  get_callers {methodName}
  get_members {typeName, nameFilter?, ...}
  get_symbol {symbolName}
  get_type_hierarchy {typeName}
  find_dead_code {scope, maxResults}
  get_diagnostics {} (whole solution; has includeAnalyzers, includeWarnings flags)
  get_code_actions {documentPath, line, column}
  rename_symbol {symbolName, newName, checkOnly:true}
  change_signature {methodId, parameterType, parameterName, defaultValue, checkOnly:true}
  remove_unused_usings {documentPath, checkOnly:true}
  apply_code_fix {documentPath, diagnosticId, checkOnly:true}
Use checkOnly:true on all mutating tools so nothing is written.

Good anchor targets to try (verify they still exist): type
VendmanagerWeb.Components.Pages.Ops.TaskManager.TaskManager; its Search*Async methods;
interface IScopedService (many implementations — good for find_references/implementations);
file VMDxDropDownBoxEdit.razor.cs for a CS0414/analyzer diagnostic in get_code_actions/
apply_code_fix.

## Shell baseline row ("without Roslynk")
For each scenario, run the closest grep/sed/dotnet equivalent (recursive grep for
references/search, sed for a symbol slice, `dotnet build` for get_diagnostics) and measure
the SAME way (median ms, text/4 tokens). Leave a cell BLANK where there is no sensible shell
equivalent (e.g. get_members, find_dead_code, get_code_actions, remove_unused_usings).

## Output table
Rows = runs, columns = tools. First row is "without Roslynk" (the shell baseline). Each
subsequent row is one MCP run, named whatever I tell you (default "Roslynk 1", "Roslynk 2", …).
Every tool header spans two sub-columns: `ms` and `tok`. In each MCP cell:
  - prefix 🟢 if the Roslynk value ≤ the baseline value in that column, else 🔴;
  - append " (X%)" where X = round(Roslynk / baseline * 100);
  - if the baseline cell is blank (no shell equivalent), show the raw Roslynk value with
    NO dot and NO percentage.
Render as GitHub-flavoured markdown. Below the table, note anything that changed since a
prior run (format shifts, tools that flipped green/red, scenarios dropped because the symbol
no longer resolves). Use blank cells, never "n/a".

Start by opening the solution and running one MCP run named "Analyzers".