namespace LocalFunctionLib;

public class ParentClass
{
	public class Widget
	{
		public int Method1(int count)
		{
			return localMethod("x", count);

			static int localMethod(string p1, int p2)
			{
				int length = inner(p1);
				return length + p2;

				static int inner(string text)
				{
					return text.Length;
				}
			}
		}

		public int Method2(int value)
		{
			return helper(value);

			static int helper(int number) => number * 2;
		}

		public int Method2(string value)
		{
			return helper(value);

			static int helper(string text) => text.Length;
		}

		public int Caller() => Method1(1) + Method2(2);
	}
}
