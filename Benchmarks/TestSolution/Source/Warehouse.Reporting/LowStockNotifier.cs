using Warehouse.Core.Inventory;

namespace Warehouse.Reporting;

public sealed class LowStockNotifier
{
	private const int Threshold = 10;

	private readonly IStockLedger Ledger;

	public LowStockNotifier(IStockLedger ledger)
	{
		Ledger = ledger;
	}

	public IReadOnlyList<string> FindLowStock()
	{
		return Ledger.Items
			.Where(item => item.OnHand < Threshold)
			.Select(item => item.Sku)
			.OrderBy(sku => sku, StringComparer.OrdinalIgnoreCase)
			.ToList();
	}
}
