namespace SimpleLibrary;

public class Holder
{
	public int Count { get; set; }

	private bool _ready;

	public string Combine(
		string first,
		string second)
	{
		return _ready ? first + second : second;
	}
}
