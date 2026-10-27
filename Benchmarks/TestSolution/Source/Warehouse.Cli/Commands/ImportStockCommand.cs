using Warehouse.Core.Inventory;

namespace Warehouse.Cli.Commands;

internal sealed class ImportStockCommand
{
	private readonly StockLedger Ledger;

	public ImportStockCommand(StockLedger ledger)
	{
		Ledger = ledger;
	}

	public int Run(string[] args)
	{
		if (args.Length == 0)
		{
			Console.Error.WriteLine("Usage: import <sku>[:<count>] ...");

			return 2;
		}

		foreach (var entry in args)
		{
			var parts = entry.Split(':', 2);
			var count = parts.Length > 1 && int.TryParse(parts[1], out var parsed) ? parsed : 1;

			// Imported stock arrives as a positive adjustment.
			Ledger.ApplyAdjustment(parts[0], count);
		}

		return 0;
	}
}
