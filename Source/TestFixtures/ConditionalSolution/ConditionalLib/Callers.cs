namespace ConditionalLib;

public class Caller2
{
#if DEBUG
	public void DebugCall() => new Target().Ping();
#else
	public void ReleaseCall() => new Target().Ping();
#endif
}
