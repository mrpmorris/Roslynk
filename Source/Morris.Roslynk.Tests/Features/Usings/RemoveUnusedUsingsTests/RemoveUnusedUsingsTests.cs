using Morris.Roslynk.Features.Usings.RemoveUnusedUsings;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.Usings.RemoveUnusedUsingsTests;

public class RemoveUnusedUsingsTests
{
	[Fact]
	public async Task WhenAFileHasAnUnnecessaryUsing_ThenItIsRemoved()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string greeter = FindFile(solutionPath, "Greeter.cs");
		await File.WriteAllTextAsync(greeter, "using System.Text;\r\n" + await File.ReadAllTextAsync(greeter));

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RemoveUnusedUsingsTool(registry, new ApplyPipeline());

		RemoveUnusedUsingsResponse response = await subject.RemoveUnusedUsings(solutionPath);

		Assert.True(response.Applied);
		Assert.True(response.RemovedCount >= 1);
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
		var subject = new RemoveUnusedUsingsTool(registry, new ApplyPipeline());

		RemoveUnusedUsingsResponse response = await subject.RemoveUnusedUsings(solutionPath, documentPath: null, checkOnly: true);

		Assert.False(response.Applied);
		Assert.NotEmpty(response.ChangedFiles);
		Assert.Equal(withUnused, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenThereAreNoUnnecessaryUsings_ThenNothingIsApplied()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new RemoveUnusedUsingsTool(registry, new ApplyPipeline());

		RemoveUnusedUsingsResponse response = await subject.RemoveUnusedUsings(TestSolutions.Simple);

		Assert.False(response.Applied);
		Assert.Equal(0, response.RemovedCount);
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
