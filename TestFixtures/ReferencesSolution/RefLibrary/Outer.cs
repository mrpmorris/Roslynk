namespace RefSpace;

public class Outer
{
	public class Nested : IThing
	{
		public IThing NestedSelf() => this;

		public IThing NestedPair()
		{
			IThing a = this;
			IThing b = this;
			return a == b ? a : b;
		}
	}
}
