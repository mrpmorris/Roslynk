namespace SimpleLibrary;

public interface IGreeter
{
	/// <summary>Builds a greeting for <paramref name="name"/>.</summary>
	/// <param name="name">Who to greet.</param>
	/// <returns>The greeting text.</returns>
	string Greet(string name);
}
