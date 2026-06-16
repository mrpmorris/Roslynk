namespace RefSpace;

public interface IThing
{
}

public class Alpha : IThing
{
	public IThing AlphaSelf() => this;

	public IThing AlphaPair()
	{
		IThing a = this;
		IThing b = this;
		return a == b ? a : b;
	}
}

public class Beta : IThing
{
	public IThing BetaSelf() => this;
}
