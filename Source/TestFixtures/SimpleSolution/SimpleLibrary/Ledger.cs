namespace SimpleLibrary;

// A note that sits above the declaration.
public partial class Ledger
{
	/// <summary>Adds <paramref name="amount"/> to the running total.</summary>
	public int Add(int amount)
	{
		Total += amount;
		return Total;
	}

	public int Add(int amount, int times)
	{
		for (int index = 0; index < times; index++)
			Add(amount);

		return Total;
	}
}
