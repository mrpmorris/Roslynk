namespace BrokenLibrary;

public class Broken
{
	// Deliberate CS0029: cannot implicitly convert 'string' to 'int'. Used by the diagnostics tests.
	public int GetValue() => "not an int";
}
