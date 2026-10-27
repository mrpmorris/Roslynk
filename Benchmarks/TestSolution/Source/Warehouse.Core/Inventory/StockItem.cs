namespace Warehouse.Core.Inventory;

public sealed class StockItem
{
	public StockItem(string sku, string name, int onHand)
	{
		Sku = sku;
		Name = name;
		OnHand = onHand;
	}

	public string Sku { get; }

	public string Name { get; }

	public int OnHand { get; private set; }

	internal void SetOnHand(int onHand)
	{
		OnHand = onHand;
	}
}
