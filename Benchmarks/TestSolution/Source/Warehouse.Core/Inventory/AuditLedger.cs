namespace Warehouse.Core.Inventory;

public sealed class AuditLedger
{
	private readonly List<string> Entries = [];

	public void ApplyAdjustment(string sku, int delta, string reason)
	{
		Entries.Add($"{DateTimeOffset.UtcNow:O} {sku} {delta:+0;-0;0} {reason}");
	}

	public IReadOnlyList<string> EntriesFor(string sku)
	{
		return Entries.Where(e => e.Contains(sku, StringComparison.OrdinalIgnoreCase)).ToList();
	}
}
