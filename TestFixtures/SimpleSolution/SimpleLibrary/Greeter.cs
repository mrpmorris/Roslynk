namespace SimpleLibrary;

public class Greeter : IGreeter
{
	/// <inheritdoc/>
	public string Greet(string name) => $"Hello, {name}!";
}
