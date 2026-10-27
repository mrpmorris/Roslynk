The C# solution for this task is at `Benchmarks/TestSolution/TestSolution.slnx` in your workspace. Report file paths relative to `Benchmarks/TestSolution/`, using forward slashes. Line numbers are 1-based.

Find the declaration of the two-parameter overload of the `ApplyAdjustment` method implemented by the `StockLedger` class in the `Warehouse.Core.Inventory` namespace, and every place in the solution that calls that specific overload.

Reply with exactly this shape and nothing else:

declaration: {$path}:{$line} — {$signature}
call-sites: {$count}
- {$path}:{$line} — called from {$type}.{$member}

Requirements:

- Report the concrete implementation declared on `StockLedger`, not the `IStockLedger` interface declaration.
- `{$signature}` is the declared signature without the access modifier, for example `void Recalculate(string region, int year)`.
- List only actual calls to the two-parameter overload. Ignore comments, XML documentation and string literals, and do not list calls to other overloads or to same-named methods on other types.
- `{$type}` and `{$member}` are the simple names of the containing type and member of each call.
- Do not modify any files.
