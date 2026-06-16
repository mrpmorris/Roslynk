using Morris.Roslynk.Features.Symbols.FindDefinition;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

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

		string result = await subject.FindDefinition(TestSolutions.Simple, callerPath, line, column);

		Assert.DoesNotContain("#error=", result);
		Assert.Contains("#fullName=SimpleLibrary.Greeter", result);
		Assert.Contains("#path=", result);
		Assert.Contains("Greeter.cs", result);
		Assert.Contains("#loc=", result);
	}

	[Fact]
	public async Task WhenGivenASolutionRelativeUsagePath_ThenTheDeclarationIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDefinitionTool(registry, new SymbolResolver());

		string solutionDir = Path.GetDirectoryName(TestSolutions.Simple)!;
		string callerPath = Path.Combine(solutionDir, "SimpleLibrary", "Caller.cs");
		string relativePath = Path.GetRelativePath(solutionDir, callerPath).Replace('\\', '/');
		string text = await File.ReadAllTextAsync(callerPath);
		(int line, int column) = ToLineColumn(text, text.IndexOf("Greeter", StringComparison.Ordinal));

		string result = await subject.FindDefinition(TestSolutions.Simple, relativePath, line, column);

		Assert.DoesNotContain("#error=", result);
		Assert.Contains("Greeter", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDefinitionTool(registry, new SymbolResolver());

		string callerPath = Path.Combine(Path.GetDirectoryName(TestSolutions.Simple)!, "SimpleLibrary", "Caller.cs");

		string result = await subject.FindDefinition(TestSolutions.Simple, callerPath, 1, 1);

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

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
