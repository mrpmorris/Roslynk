namespace Morris.Roslynk.Tests.Helpers;

/// <summary>
/// Scratch SimpleSolution whose Greeter.cs has an unused <c>using</c>, for IDE0005 / CS8019 and code-fix tests.
/// </summary>
internal static class UnnecessaryUsingScenario
{
	public static string Create(out string greeterPath, out int usingLine)
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		greeterPath = Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories).First();

		const string content =
			"using System.Collections.Generic;\r\n" +
			"\r\n" +
			"namespace SimpleLibrary;\r\n\r\n" +
			"public class Greeter : IGreeter\r\n{\r\n" +
			"\tpublic string Greet(string name) => $\"Hello, {name}!\";\r\n" +
			"}\r\n";
		File.WriteAllText(greeterPath, content);

		usingLine = 1;
		return solutionPath;
	}
}
