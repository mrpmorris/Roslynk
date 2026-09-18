namespace SimpleLibrary;

/// <summary>
/// Every shape a signature-qualified name has to tell apart: arity, parameter type, ref kind, nullability,
/// generic arity and an indexer. Each member is referenced once by <see cref="OverloadCaller"/> so the
/// dead-code scan's totals are unaffected.
/// </summary>
public class Overloads
{
	public int Pick() => 0;

	public int Pick(int value) => value;

	public int Pick(string value) => value.Length;

	public int Pick(string value, int times) => value.Length * times;

	public int Pick(int? value) => value ?? -1;

	public int Pick(ref int value) => ++value;

	public int Pick<T>(T value) => value is null ? 0 : 1;

	public int Pick(Widget widget) => widget is null ? 0 : 1;

	public int this[int index] => index;

	public int this[string key] => key.Length;
}

/// <summary>Keeps every <see cref="Overloads"/> member referenced, so none of them reads as dead code.</summary>
public class OverloadCaller
{
	public int RunAll()
	{
		var overloads = new Overloads();
		int byRef = 1;

		return overloads.Pick()
			+ overloads.Pick(1)
			+ overloads.Pick("two")
			+ overloads.Pick("three", 3)
			+ overloads.Pick((int?)4)
			+ overloads.Pick(ref byRef)
			+ overloads.Pick<long>(5)
			+ overloads.Pick(new Widget())
			+ overloads[6]
			+ overloads["seven"];
	}
}
