namespace Warehouse.Core.Inventory;

public interface IStockLedger
{
	IReadOnlyCollection<StockItem> Items { get; }

	int OnHand(string sku);

	void ApplyAdjustment(string sku, int delta);

	void ApplyAdjustment(string sku, int delta, string reason);
}
