namespace Morris.Roslynk.Tests.Helpers;

/// <summary>
/// A scratch SimpleSolution whose Greeter.cs has a method with an unused local (CS0219), giving a
/// deterministic, loaded-fixer-backed code action ("Remove unused variable") to drive code-action tests.
/// </summary>
internal static class UnusedLocalScenario
{
	public static string Create(out string greeterPath, out int unusedLine)
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		greeterPath = Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories).First();

		const string content =
			"namespace SimpleLibrary;\r\n\r\n" +
			"public class Greeter : IGreeter\r\n{\r\n" +
			"\tpublic string Greet(string name) => $\"Hello, {name}!\";\r\n\r\n" +
			"\tpublic void Stray()\r\n\t{\r\n" +
			"\t\tint unused = 0;\r\n" +
			"\t}\r\n}\r\n";
		File.WriteAllText(greeterPath, content);

		unusedLine = content[..content.IndexOf("int unused", StringComparison.Ordinal)].Count(character => character == '\n') + 1;
		return solutionPath;
	}
}
