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
| Read-only test that reads the snapshot directly (`RoslynInstance.CurrentSolution`, a resolver call) rather than through a tool | Committed fixture, or a `CreateScratch...` copy that only adds source | `await TestSolutions.SharedInstanceAsync(path)` — **never dispose** |
| Test that writes files, applies a fix, renames, patches, extracts or changes a signature | `Scratch(...)` in a `MutatingTestBase` class — a fresh copy per test | `Registry` (the base class's private registry) |
| Cold load: asserts `error=Indexing` / `status=Building` / load diagnostics | Committed or scratch | `new InstanceRegistry(new LoadWaitTimeout(TimeSpan.Zero))` — not the base class's |
| Asserts instance state: `IsDirty`, watcher errors, rebuild, eviction, reload, `OpenSolutionPaths` | Committed or scratch | `Registry` in a `MutatingTestBase` class — never the shared one |
| Asserts metrics state (`error=Computing`) or tracked-analyzer swaps | Committed or scratch | `Registry` in a `MutatingTestBase` class |
| Advances the snapshot itself (`AdvanceTo`, a queued write, a stub operation that publishes) | Committed or scratch | `Registry` in a `MutatingTestBase` class |

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
5. **Restore once per fixture, then clone.** `CreateScratch` restores a fixture once per process and
   copies its restore output into each scratch copy — the files restore writes to the top of an `obj`
   folder, with the fixture's absolute path rewritten to the copy's — so a copy has an assets file
   without spawning `dotnet restore`. A copy that a test builds re-restores, because the copied
   `project.nuget.cache` carries the old paths and NuGet treats it as stale. `Prepare` still restores
   only `Simple`, `Broken`, `References`, `Access`, `Dispose`, `Expression`, `LocalFunction`,
   `Conditional`, `LargeType` and `Cshtml` load without `obj/`; `Razor`, `RazorInject`, `Bind`,
   `RazorMultiProject`, `CodeStyle`, `Generator` and `MultiTarget` need NuGet assets. `TestSolutions`
   owns the `FixturesNeedingRestore` allowlist; do not add a per-copy restore anywhere.
6. **Do not change xUnit parallelism.** The default (one thread per core) is fastest; 8 and 32
   threads both measured ~20% slower, and higher counts make the analyzer budget flaky.
7. **Do not merge fixtures into one solution.** A shared registry already loads each fixture once;
   merging changes project counts, path resolution and name-resolution ambiguity that tests assert.
8. **Delete every scratch copy you create.** Derive the class from `MutatingTestBase` and create the
   copy with `Scratch(...)` or `ScratchAsync(...)`; `DisposeAsync` disposes the registry first, which
   releases the build host's file handles, and then deletes the copy. A class that owns its own
   `IAsyncLifetime` teardown, or an `IClassFixture` that creates the copy for its class, calls
   `ScratchDirectories.Delete(path)` after disposing the registry. Use `TrackScratch(path)` for a
   directory a test makes for itself outside `Scratch` — a moved folder, a synthetic build tree.
   A copy a shared instance keeps open for the whole run is deleted when the process exits.

   Deletion costs about 8 ms and succeeds first time when the registry is disposed first, so do not
   wrap it in a retry loop with a delay. `ScratchDirectories` retries in the background when a build
   host is slow to release a file, and the next run's `ScratchDirectorySweeper` clears anything a
   killed run left behind.

9. **Share a prepared copy when the test only reads.** When a read-only test needs a scratch copy
   that carries extra source — an unused local, an unused `@using`, a built generator — prepare it
   once and load it through the shared registry (`TestSolutions.RazorWithUnusedCode`,
   `TestSolutions.GeneratorWithBuiltGenerator`, the `CreateScratch...With...` helpers), rather than
   copying and loading it per test. A test that writes still gets a fresh copy and a private
   registry.

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

Read-only test that reads the snapshot itself (a fixture whose only write is adding source to a scratch copy):

```csharp
public async Task InitializeAsync()
{
	SolutionPath = TestSolutions.CreateScratchSimpleSolutionWithPositionalRecord();
	Instance = await TestSolutions.SharedInstanceAsync(SolutionPath);
}

public Task DisposeAsync() => Task.CompletedTask;
```

Write test on a scratch copy:

```csharp
public class RenameSymbolTests : MutatingTestBase
{
	[Fact]
	public async Task WhenRenamingAType_ThenItsDeclarationAndReferencesAreRewrittenOnDisk()
	{
		string solutionPath = Scratch(TestSolutions.CreateScratchSimpleSolution);
		await Registry.GetOrAddAsync(solutionPath);

		string result = await new RenameSymbolTool(Registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline())
			.RenameSymbol(solutionPath, "SimpleLibrary.Greeter", "Welcomer");

		Assert.Contains("applied=Y", result);
	}
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

- Does it write? → derive from `MutatingTestBase`, create the copy with `Scratch(...)`, use `Registry`.
- Does it assert or mutate instance state (cold load, dirty, watcher, reload, metrics, `AdvanceTo`)? → `Registry` in a `MutatingTestBase` class; a cold load keeps its own registry with a timeout.
- Otherwise → `TestSolutions.SharedAsync(committedFixture)` and no `using`.
- Does it create a directory of its own outside `Scratch`? → `TrackScratch(path)` so teardown removes it.
- Does the fixture have a `PackageReference`, `FrameworkReference` or `netstandard` target? → it
  needs restore; leave that to `TestSolutions`.
- Does it assert that a query returned nothing, or an exact header set? → `OutlineAssert`, not `Assert.Equal`.
- Run the class alone, then the full suite; both must pass. Compare with a trx logger rather than
  wall-clock guesswork, and remember that machine background load can double a run — paired,
  back-to-back runs are the only reliable comparison.
