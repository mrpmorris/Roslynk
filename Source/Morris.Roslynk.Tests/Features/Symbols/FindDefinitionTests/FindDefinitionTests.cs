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
		var subject = new FindDefinitionTool(registry, new SymbolResolver());

		string callerPath = Path.Combine(Path.GetDirectoryName(TestSolutions.Simple)!, "SimpleLibrary", "Caller.cs");
		string text = await File.ReadAllTextAsync(callerPath);
		(int line, int column) = ToLineColumn(text, text.IndexOf("Greeter", StringComparison.Ordinal));

		FindDefinitionResponse response = await subject.FindDefinition(TestSolutions.Simple, callerPath, line, column);

		Assert.NotNull(response.FullName);
		Assert.Contains("Greeter", response.FullName);
		Assert.EndsWith("Greeter.cs", response.SourcePath);
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
