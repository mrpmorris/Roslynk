using Morris.Roslynk.Features.DeadCode.FindDeadCode;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.DeadCode.FindDeadCodeTests;

public class FindDeadCodeTests
{
	[Fact]
	public async Task WhenAPrivateMethodIsNeverCalled_ThenItIsReportedWithHighConfidence()
	{
		string result = await RunAsync();

		Assert.DoesNotContain("#error=", result);
		Assert.Contains("method,SimpleLibrary.Widget.Unused,High", result);
	}

	[Fact]
	public async Task WhenAMethodIsReferencedOrImplementsAnInterface_ThenItIsNotReported()
	{
		string result = await RunAsync(includePublic: true);

		Assert.DoesNotContain("SimpleLibrary.Widget.Compute", result);
		Assert.DoesNotContain("SimpleLibrary.Greeter.Greet", result);
	}

	[Fact]
	public async Task WhenIncludePublicIsFalse_ThenUnreferencedPublicMembersAreNotReported()
	{
		string result = await RunAsync(includePublic: false);

		Assert.DoesNotContain("SimpleLibrary.Caller.Run", result);
	}

	[Fact]
	public async Task WhenIncludePublicIsTrue_ThenUnreferencedPublicMembersAreReported()
	{
		string result = await RunAsync(includePublic: true);

		Assert.Contains("SimpleLibrary.Caller.Run", result);
	}

	[Fact]
	public async Task WhenAScopeIsGiven_ThenOnlyMatchingSymbolsAreConsidered()
	{
		string result = await RunAsync(scope: "SimpleLibrary.Widget");

		IReadOnlyList<string> symbols = Symbols(result);
		Assert.NotEmpty(symbols);
		Assert.All(symbols, symbol => Assert.StartsWith("SimpleLibrary.Widget", symbol));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new FindDeadCodeTool(registry);

		string result = await subject.FindDeadCode(TestSolutions.Simple);

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static async Task<string> RunAsync(string? scope = null, bool includePublic = false)
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new FindDeadCodeTool(registry);

		return await subject.FindDeadCode(TestSolutions.Simple, scope, includePublic);
	}

	private static IReadOnlyList<string> Symbols(string text)
	{
		var symbols = new List<string>();
		foreach (string raw in text.Split('\n'))
		{
			if (!raw.StartsWith('\t'))
				continue;

			// \t<kind>,<fully-qualified name>,<confidence> <reason>: the name is the second comma-field.
			string[] parts = raw.TrimStart('\t').Split(',');
			if (parts.Length >= 2)
				symbols.Add(parts[1]);
		}

		return symbols;
	}
}
