namespace ConditionalLib;

public class Navigation
{
	public Target Use()
	{
#if DEBUG
		return new Target();
#else
		return new Target();
#endif
	}
}
