# Releases

## Unreleased

- Steer impact/usage questions to multi_query: impact-analysis recipe in the roslynk skill plus multi_query/find_references description hints and schema tests (Fixes #22)

## 1.1.0

- Multi query: batch multiple read-only tool calls in one request (Fixes #31)
- Ambiguous requests: clarify before acting (Fixes #34)
- New `get_symbol_body` tool (Fixes #35)
- Fix analyzer diagnostics through the code-fix path (Fixes #9)
- Defaulted tool parameters genuinely omittable (Fixes #30)
- Docs: DeepSeek harness instructions, agent configuration section, manual mcp.json setup for non-Claude AI tools

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
