# Roslynk

**Roslyn + link** — the link between Claude and Roslyn.

Roslynk gives an MCP client (e.g. Claude) semantic intelligence over C# code via Roslyn:
diagnostics, symbol navigation, find-references, semantic rename, code actions, dead-code detection
and more — operating directly on the projects compiled in a loaded solution.

- **Transport:** HTTP only, bound to loopback (`127.0.0.1`/`::1`).
- **Host:** foreground console on Linux/WSL/macOS; on Windows, also installable as a headless service. No UI.
- **Observability:** OpenTelemetry, exported via OTLP to a backend you configure.

See [Requirements.md](Requirements.md) for the full design.

## Layout

```
Source/
  App/
    Morris.Roslynk/        Features/ (vertical slices) + Infrastructure/ (shared engine)
    Morris.Roslynk.Mcp/    Windows service host (bootstrap only)
    Morris.Roslynk.Tests/
    Morris.Roslynk.McpTests/
  TestFixtures/
```

## Build

```
dotnet build Source/Morris.Roslynk.slnx
dotnet test  Source/Morris.Roslynk.slnx
```

## Run (Linux / WSL / macOS)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on `PATH`.

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
