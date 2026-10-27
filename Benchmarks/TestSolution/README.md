# TestSolution

A small warehouse-inventory solution used by every Roslynk benchmark use case. It is benchmark data, not product code: it stays fixed so both conditions of a run see exactly the same source, and it must not be modified between the two conditions.

It is self-contained: it carries its own solution file, projects and shared build settings, and references nothing outside this folder.

| Path | Contents |
| --- | --- |
| `TestSolution.slnx` | The solution: `Warehouse.Core`, `Warehouse.Cli`, `Warehouse.Reporting`. |
| `Directory.Build.props` | Shared build settings (net10.0, latest C#, nullable and implicit usings). |
| `Source/Warehouse.Core/` | Inventory and pricing model. |
| `Source/Warehouse.Cli/` | Console entry point and commands. |
| `Source/Warehouse.Reporting/` | Report and stock-take helpers. |
| `data/seed-stock.json` | Seed stock data copied next to the CLI output. |
| `scripts/reset.sh` | Restores this folder to its committed state and removes build output. |

## Running it

```bash
dotnet build TestSolution.slnx
dotnet run --project Source/Warehouse.Cli -- summary
```

## Resetting it

`scripts/reset.sh` runs `git restore` and `git clean -xdf`, scoped to this folder. Run it only inside a benchmark worktree, never in the main checkout:

```bash
Benchmarks/TestSolution/scripts/reset.sh
```
