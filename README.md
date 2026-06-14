# Roslynk

**Roslyn + link** — the link between Claude and Roslyn.

Roslynk is a Windows service that gives an MCP client (e.g. Claude) semantic intelligence over C#
code via Roslyn: diagnostics, symbol navigation, find-references, semantic rename, code actions,
dead-code detection and more — operating directly on the projects compiled in a loaded solution.

- **Transport:** HTTP only, bound to loopback (`127.0.0.1`/`::1`).
- **Host:** a headless Windows service that starts with the OS. No UI.
- **Observability:** OpenTelemetry, exported via OTLP to a backend you configure.

See [Requirements.md](Requirements.md) for the full design.

## Layout

```
Source/
  Morris.Roslynk/        Features/ (vertical slices) + Infrastructure/ (shared engine)
  Morris.Roslynk.Mcp/    Windows service host (bootstrap only)
  Morris.Roslynk.Tests/
  Morris.Roslynk.McpTests/
```

## Build

```
dotnet build Source/Morris.Roslynk.slnx
dotnet test  Source/Morris.Roslynk.slnx
```
