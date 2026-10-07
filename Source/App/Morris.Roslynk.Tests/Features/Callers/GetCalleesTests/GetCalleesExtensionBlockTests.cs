using Morris.Roslynk.Features.Callers.GetCallees;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Callers.GetCalleesTests;

/// <summary>
/// C# 14 extension blocks: a member is reported as declared inside its <c>extension(...)</c> block, so its
/// parameter list does not include the receiver (unlike a classic <c>this</c> extension method). The scratch
/// SimpleSolution targets net8.0, so the test raises its LangVersion to 14.
/// </summary>
public class GetCalleesExtensionBlockTests
{
	private const string Source = """
		using System.Collections.Generic;

		namespace SimpleLibrary.Ext14;

		public static class NumberExtensions
		{
			extension(int x)
			{
				public int Thrice() => x * 3;
				public int Thrice(int times) => x * 3 * times;
				public bool IsBig => x > 100;
			}
		}

		public class User
		{
			public void Run()
			{
				int a = 5.Thrice();
				bool b = 5.IsBig;
			}
		}
		""";

	[Fact]
	public async Task WhenAnExtensionBlockMemberIsCalled_ThenItIsReportedAsDeclaredInTheBlock()
	{
		string solution = TestSolutions.CreateScratchSimpleSolution();
		string library = Path.Combine(Path.GetDirectoryName(solution)!, "SimpleLibrary");
		string project = Path.Combine(library, "SimpleLibrary.csproj");
		await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project))
			.Replace("<Nullable>enable</Nullable>", "<Nullable>enable</Nullable><LangVersion>14</LangVersion>"));
		await File.WriteAllTextAsync(Path.Combine(library, "Ext14.cs"), Source);

		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solution);
		var subject = new GetCalleesTool(registry, new SymbolResolver(), new ProjectionService());

		string result = await subject.GetCallees(solution, "SimpleLibrary.Ext14.User.Run");

		Assert.DoesNotContain("error=", result);
		// Thrice() is overloaded within the block and declared without parameters: the receiver is not one.
		Assert.Matches(@"\tmethod,Thrice,\d+:\d+,\(\)\n", result);
		Assert.Matches(@"\tmethod,get_IsBig,\d+:\d+\n", result);
	}
}
