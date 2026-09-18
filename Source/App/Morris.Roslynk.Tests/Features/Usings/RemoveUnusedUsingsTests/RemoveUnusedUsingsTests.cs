using Morris.Roslynk.Features.Usings.RemoveUnusedUsings;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Features.Usings.RemoveUnusedUsingsTests;

public class RemoveUnusedUsingsTests
{
	[Fact]
	public async Task WhenAUsingHasACommentAboveIt_ThenTheCommentSurvives()
	{
		// The reason this tool now goes through Roslyn's own fix: removing the node took the comment with it.
		string solutionPath = UnnecessaryUsingScenario.Create(out string greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.RemoveUnusedUsings(registry);

		string result = await subject.RemoveUnusedUsings(solutionPath);

		Assert.Contains("applied=Y", result);
		string content = await File.ReadAllTextAsync(greeter);
		Assert.DoesNotContain(UnnecessaryUsingScenario.UnnecessaryUsing, content);
		Assert.Contains(UnnecessaryUsingScenario.Comment, content);
	}

	[Fact]
	public async Task WhenAFileHasAnUnnecessaryUsing_ThenItIsRemoved()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string greeter = FindFile(solutionPath, "Greeter.cs");
		await File.WriteAllTextAsync(greeter, "using System.Text;\r\n" + await File.ReadAllTextAsync(greeter));

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.RemoveUnusedUsings(registry);

		string result = await subject.RemoveUnusedUsings(solutionPath);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("removedCount=0", result);
		Assert.DoesNotContain("using System.Text;", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenCheckOnly_ThenTheUsingIsReportedButNotRemoved()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string greeter = FindFile(solutionPath, "Greeter.cs");
		string withUnused = "using System.Text;\r\n" + await File.ReadAllTextAsync(greeter);
		await File.WriteAllTextAsync(greeter, withUnused);

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.RemoveUnusedUsings(registry);

		string result = await subject.RemoveUnusedUsings(solutionPath, documentPath: null, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Greeter.cs", result);
		Assert.Equal(withUnused, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenThereAreNoUnnecessaryUsings_ThenNothingIsApplied()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = TestServices.RemoveUnusedUsings(registry);

		string result = await subject.RemoveUnusedUsings(TestSolutions.Simple);

		Assert.Contains("applied=N", result);
		Assert.Contains("removedCount=0", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = TestServices.RemoveUnusedUsings(registry);

		string result = await subject.RemoveUnusedUsings(TestSolutions.Simple);

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
