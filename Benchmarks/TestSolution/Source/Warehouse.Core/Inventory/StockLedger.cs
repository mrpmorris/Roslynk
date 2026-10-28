namespace Warehouse.Core.Inventory;

/// <summary>
/// Owns on-hand stock. Every change goes through ApplyAdjustment so the movement log
/// stays complete; nothing else may call <see cref="StockItem.SetOnHand"/>.
/// </summary>
public sealed class StockLedger : IStockLedger
{
	private readonly Dictionary<string, StockItem> ItemsBySku = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<StockMovement> Movements = [];

	public StockLedger(IEnumerable<StockItem> items)
	{
		foreach (var item in items)
		{
			ItemsBySku[item.Sku] = item;
		}
	}

	public IReadOnlyCollection<StockItem> Items => ItemsBySku.Values;

	public int OnHand(string sku)
	{
		return Find(sku).OnHand;
	}

	public void ApplyAdjustment(string sku, int delta)
	{
		ApplyAdjustment(sku, delta, "manual");
	}

	public void ApplyAdjustment(string sku, int delta, string reason)
	{
		var item = Find(sku);

		item.SetOnHand(item.OnHand + delta);
		Movements.Add(new StockMovement(item.Sku, delta, reason, DateTimeOffset.UtcNow));
	}

	public IReadOnlyList<StockMovement> MovementsFor(string sku)
	{
		return Movements.Where(m => string.Equals(m.Sku, sku, StringComparison.OrdinalIgnoreCase)).ToList();
	}

	private StockItem Find(string sku)
	{
		if (!ItemsBySku.TryGetValue(sku, out var item))
		{
			throw new KeyNotFoundException($"No stock item with SKU '{sku}'.");
		}

		return item;
	}
}
