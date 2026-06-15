using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces;

public class SolutionRelativePathTests
{
	[Fact]
	public void WhenThePathIsUnderTheSolutionDirectory_ThenItIsMadeRelativeWithForwardSlashes()
	{
		string? result = SolutionRelativePath.Of(@"C:\sln", @"C:\sln\src\App.cs");

		Assert.Equal("src/App.cs", result);
	}

	[Fact]
	public void WhenThePathIsOutsideTheSolutionDirectory_ThenItWalksOutWithDotDot()
	{
		string? result = SolutionRelativePath.Of(@"C:\sln\app", @"C:\sln\shared\Linked.cs");

		Assert.Equal("../shared/Linked.cs", result);
	}

	[Fact]
	public void WhenTheSolutionDirectoryIsUnknown_ThenTheAbsolutePathIsReturnedWithForwardSlashes()
	{
		string? result = SolutionRelativePath.Of((string?)null, @"C:\sln\src\App.cs");

		Assert.Equal("C:/sln/src/App.cs", result);
	}

	[Fact]
	public void WhenThePathIsNull_ThenNullIsReturned()
	{
		string? result = SolutionRelativePath.Of(@"C:\sln", null);

		Assert.Null(result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenARootedPath_ThenItIsReturnedAsIs()
	{
		string result = SolutionRelativePath.ToAbsolute(@"C:\sln", @"C:\other\App.cs");

		Assert.Equal(@"C:\other\App.cs", result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenARelativePath_ThenItIsResolvedAgainstTheSolutionDirectory()
	{
		string result = SolutionRelativePath.ToAbsolute(@"C:\sln", Path.Combine("src", "App.cs"));

		Assert.Equal(@"C:\sln\src\App.cs", result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenADotDotPath_ThenItWalksOutOfTheSolutionDirectory()
	{
		string result = SolutionRelativePath.ToAbsolute(@"C:\sln\app", Path.Combine("..", "shared", "B.cs"));

		Assert.Equal(@"C:\sln\shared\B.cs", result);
	}

	[Fact]
	public void WhenToAbsoluteIsGivenForwardSlashes_ThenTheyAreConvertedToTheOsDelimiter()
	{
		string result = SolutionRelativePath.ToAbsolute(@"C:\sln", "src/nested/App.cs");

		Assert.Equal(Path.Combine(@"C:\sln", "src", "nested", "App.cs"), result);
	}
}
