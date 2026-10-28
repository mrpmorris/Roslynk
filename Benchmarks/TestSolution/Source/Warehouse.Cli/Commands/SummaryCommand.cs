using Warehouse.Core.Inventory;

namespace Warehouse.Cli.Commands;

internal sealed class SummaryCommand
{
	private readonly IStockLedger Ledger;

	public SummaryCommand(IStockLedger ledger)
	{
		Ledger = ledger;
	}

	public int Run()
	{
		foreach (var item in Ledger.Items.OrderBy(i => i.Sku, StringComparer.OrdinalIgnoreCase))
		{
			Console.WriteLine($"{item.Sku}\t{item.Name}\t{item.OnHand}");
		}

		return 0;
	}
}
