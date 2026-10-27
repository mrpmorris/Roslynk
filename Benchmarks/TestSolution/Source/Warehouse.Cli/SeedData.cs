using System.Text.Json;
using Warehouse.Core.Inventory;

namespace Warehouse.Cli;

internal static class SeedData
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	public static IReadOnlyList<StockItem> Load(string path)
	{
		if (!File.Exists(path))
		{
			return [];
		}

		using var stream = File.OpenRead(path);
		var seeds = JsonSerializer.Deserialize<List<SeedItem>>(stream, JsonOptions) ?? [];

		return seeds.Select(seed => new StockItem(seed.Sku, seed.Name, seed.OnHand)).ToList();
	}

	private sealed record SeedItem(string Sku, string Name, int OnHand);
}
