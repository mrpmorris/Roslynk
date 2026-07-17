# Roslynk

**Roslyn + link** — the link between Claude and Roslyn.

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

From the published NuGet package (no clone, no build):

```bash
claude mcp add roslynk -- dnx Roslynk --yes -- stdio
```

Tagged releases (e.g. `1.0.0-beta.1`, no `v` prefix) are packed and pushed to nuget.org by CI.

From a source checkout:

```bash
claude mcp add roslynk -- dotnet run --project /path/to/Roslynk/Source/App/Morris.Roslynk.Mcp -- stdio
```

(With a published build, use `Morris.Roslynk.Mcp stdio` as the command instead — it starts faster.)

The daemon outlives individual sessions so Roslyn workspaces stay warm across clients. Its console
output goes to `Roslynk/daemon.log` under the local application-data folder (`~/.local/share` on
Linux, `~/Library/Application Support` on macOS, `%LOCALAPPDATA%` on Windows).

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

Installed service: see [Source/WindowsServiceInstaller/README.md](Source/WindowsServiceInstaller/README.md).
