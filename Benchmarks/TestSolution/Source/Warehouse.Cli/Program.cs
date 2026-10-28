using Warehouse.Cli.Commands;
using Warehouse.Core.Inventory;

namespace Warehouse.Cli;

internal static class Program
{
	private static int Main(string[] args)
	{
		var seedPath = Path.Combine(AppContext.BaseDirectory, "data", "seed-stock.json");
		var ledger = new StockLedger(SeedData.Load(seedPath));
		var audit = new AuditLedger();
		var command = args.Length == 0 ? "summary" : args[0];

		return command switch
		{
			"adjust" => new AdjustStockCommand(ledger, audit).Run(args[1..]),
			"import" => new ImportStockCommand(ledger).Run(args[1..]),
			"summary" => new SummaryCommand(ledger).Run(),
			_ => Unknown(command),
		};
	}

	private static int Unknown(string command)
	{
		Console.Error.WriteLine($"Unknown command '{command}'. Use adjust, import or summary.");

		return 2;
	}
}
