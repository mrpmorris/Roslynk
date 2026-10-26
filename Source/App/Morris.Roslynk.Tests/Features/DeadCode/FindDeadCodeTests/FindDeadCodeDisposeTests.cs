using Morris.Roslynk.Features.DeadCode.FindDeadCode;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.DeadCode.FindDeadCodeTests;

public class FindDeadCodeDisposeTests
{
	[Fact]
	public async Task WhenADisposableLocalIsDeclaredWithoutUsing_ThenTheDisposeIsReportedDead()
	{
		string result = await RunAsync("DisposeLib.PlainResource");

		Assert.DoesNotContain("error=", result);
		Assert.Contains("method,Dispose,", result);
	}

	[Fact]
	public async Task WhenTheDisposeIsCalledExplicitly_ThenItIsNotReportedDead()
	{
		string result = await RunAsync("DisposeLib.CalledResource");

		Assert.DoesNotContain("error=", result);
		Assert.DoesNotContain("method,Dispose,", result);
	}

	private static async Task<string> RunAsync(string scope)
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Dispose);
		var subject = new FindDeadCodeTool(registry);

		return await subject.FindDeadCode(TestSolutions.Dispose, scope, includePublic: true);
	}
}
