using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Resolution;

public class DocumentPathResolverTests
{
	[Fact]
	public async Task WhenAPathIsAnExactSolutionRelativeMatch_ThenItIsNotResolvedBySuffix()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, "SimpleLibrary/Calculator.cs");

		Assert.NotNull(resolved);
		Assert.False(resolved.ResolvedBySuffix);
		Assert.Equal("SimpleLibrary/Calculator.cs", resolved.RelativePath);
		Assert.True(Path.IsPathRooted(resolved.AbsolutePath));
	}

	[Fact]
	public async Task WhenAPathIsAnAbsoluteMatch_ThenItIsNotResolvedBySuffix()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);
		string absolute = Path.Combine(Path.GetDirectoryName(TestSolutions.Simple)!, "SimpleLibrary", "Calculator.cs");

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, absolute);

		Assert.NotNull(resolved);
		Assert.False(resolved.ResolvedBySuffix);
		Assert.Equal("SimpleLibrary/Calculator.cs", resolved.RelativePath);
	}

	[Fact]
	public async Task WhenABareFileNameMatchesExactlyOneFile_ThenItIsResolvedBySuffix()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, "Calculator.cs");

		Assert.NotNull(resolved);
		Assert.True(resolved.ResolvedBySuffix);
		Assert.Equal("SimpleLibrary/Calculator.cs", resolved.RelativePath);
	}

	[Fact]
	public async Task WhenAPathSuffixMatchesExactlyOneFile_ThenItIsResolvedBySuffix()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		Directory.CreateDirectory(Path.Combine(projectDirectory, "Nested"));
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Nested", "Only.cs"),
			"namespace SimpleLibrary;\n\npublic class Only\n{\n}\n");
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, "Nested/Only.cs");

		Assert.NotNull(resolved);
		Assert.True(resolved.ResolvedBySuffix);
		Assert.Equal("SimpleLibrary/Nested/Only.cs", resolved.RelativePath);
	}

	[Fact]
	public async Task WhenABareFileNameMatchesSeveralFiles_ThenNothingResolvesAndEveryMatchIsReported()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		string projectDirectory = Path.Combine(Path.GetDirectoryName(solutionPath)!, "SimpleLibrary");
		Directory.CreateDirectory(Path.Combine(projectDirectory, "Other"));
		await File.WriteAllTextAsync(
			Path.Combine(projectDirectory, "Other", "Widget.cs"),
			"namespace SimpleLibrary;\n\npublic class OtherWidget\n{\n}\n");
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(solutionPath);

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, "Widget.cs");
		IReadOnlyList<string> matches = DocumentPathResolver.Matches(instance.CurrentSolution, "Widget.cs");

		Assert.Null(resolved);
		Assert.Equal(
			["SimpleLibrary/Other/Widget.cs", "SimpleLibrary/Widget.cs"],
			matches.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task WhenARazorAdditionalDocumentIsNamedByFileName_ThenItResolvesToTheRazorPath()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Razor);

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, "Counter.razor");

		Assert.NotNull(resolved);
		Assert.True(resolved.ResolvedBySuffix);
		Assert.Equal("RazorLib/Counter.razor", resolved.RelativePath);
	}

	[Fact]
	public async Task WhenAPathMatchesNothing_ThenCandidatesAreOffered()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Simple);

		ResolvedDocumentPath? resolved = DocumentPathResolver.Resolve(instance.CurrentSolution, "Nope/Missing.cs");
		IReadOnlyList<string> candidates = DocumentPathResolver.Candidates(instance.CurrentSolution, "Nope/Missing.cs");

		Assert.Null(resolved);
		Assert.NotEmpty(candidates);
		Assert.Contains("SimpleLibrary/Calculator.cs", candidates);
	}
}
