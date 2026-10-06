namespace BindLib;

public class Item
{
	public string Name { get; set; } = "";
	public int Amount { get; set; }
	public string Title { get; set; } = "";
	public int Level { get; set; }
	public string Code { get; set; } = "";
	public string Tag { get; set; } = "";
	public string Note { get; set; } = "";
}

public static class Resources
{
	public static string Code => "Code";
	public static string Name => "Name";
}

public class ItemService
{
	public string Describe(Item item) => item.Name + item.Amount + item.Title + item.Level + item.Code + item.Tag + item.Note;
}
