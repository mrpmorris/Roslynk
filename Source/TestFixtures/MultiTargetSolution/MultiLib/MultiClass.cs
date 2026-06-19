namespace MultiLib;

public class MultiClass
{
#if NET8_0
	// A deliberate CS0029 present only in the net8.0 compilation, to prove per-framework diagnostics.
	public int Value => "not an int";
#else
	public int Value => 0;
#endif
}
