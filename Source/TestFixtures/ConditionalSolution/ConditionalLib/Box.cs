namespace ConditionalLib;

public class Box
{
	public int Always;

#if DEBUG
	public int DebugOnly;
#else
	public int ReleaseOnly;
#endif
}
