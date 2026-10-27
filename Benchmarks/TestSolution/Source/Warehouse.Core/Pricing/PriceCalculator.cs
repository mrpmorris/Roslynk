using Warehouse.Core.Inventory;

namespace Warehouse.Core.Pricing;

public sealed class PriceCalculator
{
	private const int LowStockThreshold = 10;

	private readonly IStockLedger Ledger;

	public PriceCalculator(IStockLedger ledger)
	{
		Ledger = ledger;
	}

	public decimal UnitPrice(decimal listPrice, string sku)
	{
		return Ledger.OnHand(sku) < LowStockThreshold ? listPrice * 1.15m : listPrice;
	}
}
