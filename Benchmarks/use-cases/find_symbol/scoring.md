# find_symbol scoring

The expected answer and scoring criteria for the `find_symbol` use case. The prompt sent to a measured run is `prompt.md` in this folder; this file is used to judge the answer.

## Expected answer

```text
declaration: Source/Warehouse.Core/Inventory/StockLedger.cs:27 — void ApplyAdjustment(string sku, int delta)
call-sites: 3
- Source/Warehouse.Cli/Commands/AdjustStockCommand.cs:27 — called from AdjustStockCommand.Run
- Source/Warehouse.Cli/Commands/ImportStockCommand.cs:29 — called from ImportStockCommand.Run
- Source/Warehouse.Reporting/StockSummaryWriter.cs:39 — called from StockSummaryWriter.Reconcile
```

## Criteria

| Criterion | Expected |
| --- | --- |
| Declaration path | `Source/Warehouse.Core/Inventory/StockLedger.cs` |
| Declaration line | 27 |
| Signature | `void ApplyAdjustment(string sku, int delta)` |
| Call-site count | 3 |
| Call site 1 | `Source/Warehouse.Cli/Commands/AdjustStockCommand.cs:27`, called from `AdjustStockCommand.Run` |
| Call site 2 | `Source/Warehouse.Cli/Commands/ImportStockCommand.cs:29`, called from `ImportStockCommand.Run` |
| Call site 3 | `Source/Warehouse.Reporting/StockSummaryWriter.cs:39`, called from `StockSummaryWriter.Reconcile` |
| No false positives | No other file or line is reported as a declaration or call site |

Score each criterion as pass or fail. The answer passes only when every criterion passes; a missing call site makes it incomplete, an extra one makes it incorrect. Do not repair the answer before scoring it.

## Not part of the expected answer

A text search will also find these, and none of them is a declaration or call site of the two-parameter overload:

- `Source/Warehouse.Core/Inventory/IStockLedger.cs:9` — the interface declaration of the same overload.
- `Source/Warehouse.Core/Inventory/IStockLedger.cs:11` — the interface declaration of the three-parameter overload.
- `Source/Warehouse.Core/Inventory/StockLedger.cs:29` — the two-parameter overload's call to the three-parameter overload (the reverse direction).
- `Source/Warehouse.Core/Inventory/StockLedger.cs:32` — the three-parameter overload declared on `StockLedger`.
- `Source/Warehouse.Core/Inventory/AuditLedger.cs:7` — a same-named method on a different type, called at `Source/Warehouse.Cli/Commands/AdjustStockCommand.cs:28`.
- `Source/Warehouse.Core/Inventory/StockLedger.cs:4` — a documentation comment.
- `Source/Warehouse.Reporting/StockSummaryWriter.cs:40` — a commented-out call.

## Provenance

This reference was verified against the committed `TestSolution` with Roslynk: `find_definition` returned the declaration above and `find_references` on `Warehouse.Core.Inventory.StockLedger.ApplyAdjustment(string, int)` returned exactly the three call sites. If `TestSolution` changes, re-derive this file from the new source before running the benchmark.
