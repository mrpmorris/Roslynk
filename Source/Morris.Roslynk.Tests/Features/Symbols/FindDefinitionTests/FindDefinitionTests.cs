using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.FindDefinitionTests;

public class FindDefinitionTests
{
	[Fact]
	public async Task WhenGivenAUsagePosition_ThenTheDeclarationIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDefinitionTool(registry, new SymbolResolver());

		string callerPath = Path.Combine(Path.GetDirectoryName(TestSolutions.Simple)!, "SimpleLibrary", "Caller.cs");
		string text = await File.ReadAllTextAsync(callerPath);
		(int line, int column) = ToLineColumn(text, text.IndexOf("Greeter", StringComparison.Ordinal));

		FindDefinitionResult result = await subject.FindDefinition(TestSolutions.Simple, callerPath, line, column);

		Assert.True(result.IsSuccess);
		Assert.NotNull(result.FullName);
		Assert.Contains("Greeter", result.FullName);
		Assert.EndsWith("Greeter.cs", result.SourcePath);
	}

	[Fact]
	public async Task WhenGivenASolutionRelativeUsagePath_ThenTheDeclarationIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDefinitionTool(registry, new SymbolResolver());

		string solutionDir = Path.GetDirectoryName(TestSolutions.Simple)!;
		string callerPath = Path.Combine(solutionDir, "SimpleLibrary", "Caller.cs");
		string relativePath = Path.GetRelativePath(solutionDir, callerPath);
		string text = await File.ReadAllTextAsync(callerPath);
		(int line, int column) = ToLineColumn(text, text.IndexOf("Greeter", StringComparison.Ordinal));

		FindDefinitionResult result = await subject.FindDefinition(TestSolutions.Simple, relativePath, line, column);

		Assert.True(result.IsSuccess);
		Assert.Contains("Greeter", result.FullName!);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDefinitionTool(registry, new SymbolResolver());

		string callerPath = Path.Combine(Path.GetDirectoryName(TestSolutions.Simple)!, "SimpleLibrary", "Caller.cs");

		FindDefinitionResult result = await subject.FindDefinition(TestSolutions.Simple, callerPath, 1, 1);

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static (int Line, int Column) ToLineColumn(string text, int index)
	{
		int line = 1;
		int column = 1;
		for (int i = 0; i < index; i++)
		{
			if (text[i] == '\n')
			{
				line++;
				column = 1;
			}
			else
			{
				column++;
			}
		}

		return (line, column);
	}
}
