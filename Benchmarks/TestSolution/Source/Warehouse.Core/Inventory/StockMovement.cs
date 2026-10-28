namespace Warehouse.Core.Inventory;

public sealed record StockMovement(string Sku, int Delta, string Reason, DateTimeOffset RecordedAt);
