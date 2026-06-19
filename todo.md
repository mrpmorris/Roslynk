# Roslynk Code Review Todos

## 🔴 High Priority

- [ ] Fix pre-existing test failures: `ClearCacheTests` and `CloseSolutionTests` reference namespaces removed from the main library — update, skip, or remove
- [ ] Thread `CancellationToken` through all tool calls that accept it but don't pass it (e.g. `GetDiagnosticsTool`, `FindDeadCodeTool`, `GetCodeActionsTool`, `ApplyCodeFixTool`)
- [ ] Update `SolutionWorkspace` after writes — the `MSBuildWorkspace` reference is never refreshed when the in-memory `Solution` advances

## 🟡 Medium Priority

- [ ] Avoid blocking readers during incremental file watcher folds — `FoldTextAsync` takes the write lock
- [ ] Pass `CancellationToken` to `SymbolFinder.FindReferencesAsync` in `FindDeadCodeTool.EvaluateAsync`
- [ ] Improve `ApplyPatchTool.ResolveDocument` suffix-match ambiguity — return an error when multiple files match rather than silently returning null
- [ ] Parallelize `SymbolResolver.FindByFullyQualifiedNameAsync` for large solutions
- [ ] Use `catch` filters in `CodeActionService.DiscoverAsync` to avoid swallowing fatal exceptions
- [ ] Align generated-file suffix patterns: `ApplyPipeline.IsGenerated` checks only `.g.cs` but `FindDeadCodeTool` also checks `.g.i.cs`, `.generated.cs`, `.designer.cs`

## 🟢 Lower Priority

- [ ] Format the result of `ChangeSignatureTool` with `Simplifier`/`Formatter` for consistent parameter indentation
- [ ] Add tests for: write-safety crash rollback, instance eviction, file watcher, concurrent access, reload_solution
- [ ] Consider whether `SymbolResolver` and `DiagnosticsService` should be scoped rather than singleton
- [ ] Document `RoslynInstance.ConsumeAsync` lifetime guarantees so the fire-and-forget consumer task is understood
