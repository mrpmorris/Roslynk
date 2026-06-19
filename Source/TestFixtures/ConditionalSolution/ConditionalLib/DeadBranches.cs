namespace ConditionalLib;

public class DeadBranches
{
	public void M()
	{
#if NEVERDEFINED
		System.Console.WriteLine("never compiled in any configuration");
#endif
	}
}
