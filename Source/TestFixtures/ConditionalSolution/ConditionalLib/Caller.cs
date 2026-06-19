namespace ConditionalLib;

public class Caller
{
	public void Run()
	{
		var target = new Target();
#if DEBUG
		target.Ping();
#else
		target.Ping();
#endif
	}
}
