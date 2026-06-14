namespace SimpleLibrary;

public class Widget
{
	/// <summary>Doubles <paramref name="value"/> as an <see cref="System.Int32"/>.</summary>
	/// <param name="value">The input value.</param>
	/// <returns>Twice the input.</returns>
	public int Compute(int value) => value * 2;

	public int UseCompute() => Compute(21);

	private int Unused() => 42;
}
