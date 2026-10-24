using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.SolutionWorkspaceTests;

public class LoadAsyncTests
{
	[Fact]
	public async Task WhenLoadingASolution_ThenItsProjectsAndDocumentsAreAvailable()
	{
		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(TestSolutions.Simple);

		Project project = Assert.Single(subject.Solution.Projects);

		Assert.Equal("SimpleLibrary", project.Name);
		Assert.Contains(project.Documents, document => document.Name == "Greeter.cs");
	}

	[Fact]
	public async Task WhenLoadingASolutionWithoutRestoreWarnings_ThenNoLoadWarningsAreReported()
	{
		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(TestSolutions.Simple);

		Assert.Empty(subject.LoadWarnings);
	}
}
