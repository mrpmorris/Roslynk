namespace SimpleLibrary;

public class Widget
{
	public int Compute(int value) => value * 2;

	public int UseCompute() => Compute(21);

	private int Unused() => GetHashCode();
}
