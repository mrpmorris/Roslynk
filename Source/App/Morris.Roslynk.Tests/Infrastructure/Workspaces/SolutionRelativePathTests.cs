using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces;

public class SolutionRelativePathTests
{
	private static string Abs(params string[] segments) =>
		Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(segments));

	[Fact]
	public void WhenThePathIsUnderTheSolutionDirectory_ThenItIsMadeRelativeWithForwardSlashes()
	{
		string? result = SolutionRelativePath.Of(Abs("sln"), Abs("sln", "src", "App.cs"));

		Assert.Equal("src/App.cs", result);
	}

	[Fact]
	public void WhenThePathIsOutsideTheSolutionDirectory_ThenItWalksOutWithDotDot()
	{
		string? result = SolutionRelativePath.Of(Abs("sln", "app"), Abs("sln", "shared", "Linked.cs"));

		Assert.Equal("../shared/Linked.cs", result);
	}

	[Fact]
	public void WhenTheSolutionDirectoryIsUnknown_ThenTheAbsolutePathIsReturnedWithForwardSlashes()
	{
		string absolutePath = Abs("sln", "src", "App.cs");

		string? result = SolutionRelativePath.Of((string?)null, absolutePath);

		Assert.Equal(absolutePath.Replace('\\', '/'), result);
	}

	[Fact]
	public void WhenThePathIsNull_ThenNullIsReturned()
	{
		string? result = SolutionRelativePath.Of(Abs("sln"), null);

		Assert.Null(result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenARootedPath_ThenItIsReturnedAsIs()
	{
		string rootedPath = Abs("other", "App.cs");

		string result = SolutionRelativePath.ToAbsolute(Abs("sln"), rootedPath);

		Assert.Equal(rootedPath, result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenARelativePath_ThenItIsResolvedAgainstTheSolutionDirectory()
	{
		string result = SolutionRelativePath.ToAbsolute(Abs("sln"), Path.Combine("src", "App.cs"));

		Assert.Equal(Abs("sln", "src", "App.cs"), result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenADotDotPath_ThenItWalksOutOfTheSolutionDirectory()
	{
		string result = SolutionRelativePath.ToAbsolute(Abs("sln", "app"), Path.Combine("..", "shared", "B.cs"));

		Assert.Equal(Abs("sln", "shared", "B.cs"), result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenForwardSlashes_ThenTheyAreConvertedToTheOsDelimiter()
	{
		string result = SolutionRelativePath.ToAbsolute(Abs("sln"), "src/nested/App.cs");

		Assert.Equal(Abs("sln", "src", "nested", "App.cs"), result);
	}
}
