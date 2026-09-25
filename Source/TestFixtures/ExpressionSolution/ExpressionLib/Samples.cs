namespace ExpressionLib;

/// <summary>Formats values for display.</summary>
public static class Formatter
{
	public const int Width = 4 * 10;

	/// <summary>Formats an <see cref="int"/> value.</summary>
	public static string Format(int value) => value.ToString();

	/// <summary>Formats a <see cref="string"/> value.</summary>
	public static string Format(string value) => value;

	public static T Echo<T>(T value) => value;

	public static int Twice(this int value) => value * 2;
}

public readonly struct Meters
{
	public Meters(double value) => Value = value;

	public double Value { get; }

	public static implicit operator double(Meters meters) => meters.Value;
}

public class Samples
{
	public string? MaybeName { get; set; }

	public void Run()
	{
		var text = Formatter.Format(42);
		string label = Formatter.Format("x");
		long widened = Formatter.Width;
		double raw = new Meters(3);
		var echoed = Formatter.Echo(text);
		int doubled = 5.Twice();
		if (MaybeName is not null)
		{
			int length = MaybeName.Length;
		}
		string? name = MaybeName;
		object unresolved = Formatter.Format(1.5);
	}
}
