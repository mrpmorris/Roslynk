namespace AccessSpace;

public class Counter
{
	public int Total = 1;
	public int Count { get; set; }
	public string? Name { get; init; }

	public Counter()
	{
		Total = 0;
		this.Count = 0;
	}

	public void Mutate(int amount, Other other)
	{
		int copy = Total;
		Total = amount;
		Total += amount;
		Total++;
		Bump(ref Total);
		Set(out Total);
		(Total, copy) = (copy, Total);
		other.Total = 5;
		Use(nameof(Total).Length);
		amount = copy;
		amount -= 1;
		Use(amount);
		Use(value: copy);
		System.Action later = () => Total = 3;
	}

	public void Use(int value) => _ = value;

	public Counter Make() => new Counter { Name = "x" };

	private static void Bump(ref int value) => value++;

	private static void Set(out int value) => value = 1;
}

public class Other
{
	public int Total;
}
