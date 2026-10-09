namespace ConsumerLib;

public static class UsesGenerated
{
	public static string Greeting() => GeneratedNamespace.Hello.Greeting;

	public static string CsvGreeting() => GeneratedNamespace.GreetingCsv.FirstLine;
}
