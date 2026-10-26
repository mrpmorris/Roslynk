# Writing fast Roslynk tests

This folder holds the engine's behavioural tests. Each test that touches a solution pays for an
MSBuild load, a first compilation and a first declaration index; those costs dominate the suite.
Reusing one loaded instance across the tests that only read it is what keeps the suite fast.

## The one rule

**Never load the same unchanged solution twice when the test only reads it.**

A test that only reads a committed fixture gets the process-wide shared registry. A test that writes,
or that asserts instance state, gets its own registry and its own scratch copy.

## Choose by test kind

| Test kind | Fixture | Registry |
|---|---|---|
| Read-only query (`get_symbol`, `get_members`, `search_symbols`, `find_references`, `get_callers`, `get_callees`, `get_diagnostics`, `get_expression_info`, `get_type_hierarchy`, `find_definition`, `find_implementations`, `get_symbol_body`, `find_dead_code`, `find_dead_conditionals`, `get_code_actions`) | Committed fixture (`TestSolutions.Simple`, `.Razor`, `.Cshtml`, …) | `await TestSolutions.SharedAsync(path)` — **never dispose** |
| Test that writes files, applies a fix, renames, patches, extracts or changes a signature | `TestSolutions.CreateScratch...()` — a fresh copy per test | `new InstanceRegistry()` |
| Cold load: asserts `error=Indexing` / `status=Building` / load diagnostics | Committed or scratch | `new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero))` |
| Asserts instance state: `IsDirty`, watcher errors, rebuild, eviction, reload, `OpenSolutionPaths` | Committed or scratch | `new InstanceRegistry()` — never the shared one |
| Asserts metrics state (`error=Computing`) or tracked-analyzer swaps | Committed or scratch | `new InstanceRegistry()` |
| Advances the snapshot itself (`AdvanceTo`, a queued write, a stub operation that publishes) | Committed or scratch | `new InstanceRegistry()` |

## Rules

1. **Never write to a committed fixture.** Tests that write must use `CreateScratch...`. `git status`
   must stay clean after a full run.
2. **Never dispose the shared registry.** Write `var registry = await TestSolutions.SharedAsync(path);`
   without `using`; disposing it tears down instances other tests are using.
3. **Use a private registry whenever the test asserts or mutates instance state** — cold load,
   `Building`, `Indexing`, `IsDirty`, watcher diagnostics, reload, eviction, metrics `Computing`,
   tracked analyzers, `OpenSolutionPaths`, or an operation that publishes a new snapshot. Sharing
   those poisons later tests.
4. **A shared snapshot's status is not part of what a query produced.** The shared instance is
   republished as `Building` while a concurrent diagnostics build compiles it, so a result can carry
   a `status=Building` header beside an otherwise complete (or empty) body. Assert on the content:
   - empty result → `OutlineAssert.HasNoSymbols(result)`, not `Assert.Equal("", result)`;
   - exact header set → `OutlineAssert.HeadersAre(expected, result)`, which drops the status line.
5. **Restore only package-bearing fixtures.** `Simple`, `Broken`, `References`, `Access`, `Dispose`,
   `Expression`, `LocalFunction`, `Conditional`, `LargeType` and `Cshtml` load cleanly without
   `obj/`; `Razor`, `RazorInject`, `Bind`, `RazorMultiProject`, `CodeStyle`, `Generator` and
   `MultiTarget` need NuGet assets. `TestSolutions` owns the `FixturesNeedingRestore` allowlist; do
   not add a restore to a package-free fixture.
6. **Do not change xUnit parallelism.** The default (one thread per core) is fastest; 8 and 32
   threads both measured ~20% slower, and higher counts make the analyzer budget flaky.
7. **Do not merge fixtures into one solution.** A shared registry already loads each fixture once;
   merging changes project counts, path resolution and name-resolution ambiguity that tests assert.

## Patterns

Read-only test on a committed fixture:

```csharp
[Fact]
public async Task WhenAMethodCallsOtherMembers_ThenTheCalleesAreReturned()
{
	var registry = await TestSolutions.SharedAsync(TestSolutions.Simple);
	var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

	string result = await subject.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

	Assert.Contains("resolvedSymbol=SimpleLibrary.Caller.Run", result);
	Assert.DoesNotContain("error=", result);
}
```

Write test on a scratch copy:

```csharp
[Fact]
public async Task WhenRenamingAType_ThenItsDeclarationAndReferencesAreRewrittenOnDisk()
{
	string solutionPath = TestSolutions.CreateScratchSimpleSolution();
	using var registry = new InstanceRegistry();
	await registry.GetOrAddAsync(solutionPath);

	string result = await new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline())
		.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

	Assert.Contains("applied=Y", result);
}
```

Cold-load test (must not use the shared registry):

```csharp
[Fact]
public async Task WhenTheLoadWaitTimeoutElapses_ThenIndexingIsReturned()
{
	using var registry = new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero));

	string result = await new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService())
		.GetCallees(TestSolutions.Simple, "SimpleLibrary.Caller.Run");

	Assert.Contains("error=Indexing", result);
	Assert.Contains("status=Building", result);

	await registry.GetOrAddAsync(TestSolutions.Simple);
}
```

## Checklist for a new test

- Does it write? → scratch copy (`CreateScratch...`), private registry.
- Does it assert or mutate instance state (cold load, dirty, watcher, reload, metrics, `AdvanceTo`)? → private registry.
- Otherwise → `TestSolutions.SharedAsync(committedFixture)` and no `using`.
- Does the fixture have a `PackageReference`, `FrameworkReference` or `netstandard` target? → it
  needs restore; leave that to `TestSolutions`.
- Does it assert that a query returned nothing, or an exact header set? → `OutlineAssert`, not `Assert.Equal`.
- Run the class alone, then the full suite; both must pass. Compare with a trx logger rather than
  wall-clock guesswork, and remember that machine background load can double a run — paired,
  back-to-back runs are the only reliable comparison.
