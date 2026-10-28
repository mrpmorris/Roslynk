using System.Text;
using Warehouse.Core.Inventory;

namespace Warehouse.Reporting;

public sealed class StockSummaryWriter
{
	private readonly IStockLedger Ledger;

	public StockSummaryWriter(IStockLedger ledger)
	{
		Ledger = ledger;
	}

	public string Write()
	{
		var builder = new StringBuilder();

		builder.AppendLine("sku,name,onHand");

		foreach (var item in Ledger.Items.OrderBy(i => i.Sku, StringComparer.OrdinalIgnoreCase))
		{
			builder.AppendLine($"{item.Sku},{item.Name},{item.OnHand}");
		}

		return builder.ToString();
	}

	public void Reconcile(string sku, int countedOnHand)
	{
		var difference = countedOnHand - Ledger.OnHand(sku);

		if (difference == 0)
		{
			return;
		}

		// Stock-take difference; the ledger records why it moved.
		Ledger.ApplyAdjustment(sku, difference);
		// Ledger.ApplyAdjustment(sku, -difference);  // superseded by the call above
	}
}
