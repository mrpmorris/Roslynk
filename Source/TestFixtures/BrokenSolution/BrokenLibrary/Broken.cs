namespace BrokenLibrary;

public class Broken
{
	// Deliberate CS0169 warning (field is never used), so the diagnostics tests can exercise severity filtering.
	private int unusedField;

	// Deliberate CS0029: cannot implicitly convert 'string' to 'int'. Used by the diagnostics tests.
	public int GetValue() => "not an int";
}
