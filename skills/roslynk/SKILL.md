---
name: roslynk
description: Use Roslynk for C#/.NET code review, investigation, compiler diagnostics, symbol navigation, and semantic edits when its MCP server is available. Applies to .cs, .razor, and .cshtml code — checking compilation, finding definitions, references, callers or implementations, renaming, applying fixes or refactorings, and dead-code analysis — even when the user never names Roslynk. Prefer it over grep, file reads, hand edits, and dotnet build for those questions.
---

Use Roslynk for semantic code questions and edits. Discover the
relevant MCP tools and follow their server instructions and tool
descriptions.

Use Roslynk compiler diagnostics instead of running a build.
Use `dotnet test` when runtime behavior needs validation.

Standard tools may inspect diffs, non-code files, and bounded
source excerpts at known locations.
