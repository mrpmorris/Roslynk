namespace Morris.Roslynk.Tests.Helpers;

/// <summary>
/// A scratch CodeStyleSolution - the fixture with <c>EnforceCodeStyleInBuild</c>, so the IDE analyzers are
/// referenced - whose Greeter.cs has an unnecessary <c>using System.Text;</c> with a comment above it.
/// The comment is what tells a trivia-preserving rewrite apart from a node removal.
/// </summary>
internal static class UnnecessaryUsingScenario
{
	public const string Comment = "// Keeps the greeting format in one place.";
	public const string UnnecessaryUsing = "using System.Text;";

	/// <summary>The 1-based line the unnecessary using sits on, below the comment.</summary>
	public const int UsingLine = 2;

	public static string Create(out string greeterPath)
	{
		string solutionPath = TestSolutions.CreateScratchCodeStyleSolution();
		greeterPath = Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories).First();
		return solutionPath;
	}
}
