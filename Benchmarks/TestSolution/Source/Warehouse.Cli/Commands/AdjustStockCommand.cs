using Warehouse.Core.Inventory;

namespace Warehouse.Cli.Commands;

internal sealed class AdjustStockCommand
{
	private readonly IStockLedger Ledger;
	private readonly AuditLedger Audit;

	public AdjustStockCommand(IStockLedger ledger, AuditLedger audit)
	{
		Ledger = ledger;
		Audit = audit;
	}

	public int Run(string[] args)
	{
		if (args.Length < 2 || !int.TryParse(args[1], out var delta))
		{
			Console.Error.WriteLine("Usage: adjust <sku> <delta> [reason]");

			return 2;
		}

		var reason = args.Length > 2 ? args[2] : "cli";

		Ledger.ApplyAdjustment(args[0], delta);
		Audit.ApplyAdjustment(args[0], delta, reason);

		Console.WriteLine($"{args[0]} now {Ledger.OnHand(args[0])}");

		return 0;
	}
}
