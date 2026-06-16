using System.Text.RegularExpressions;
using Morris.Roslynk.Features.DeadCode.FindDeadCode;
using Morris.Roslynk.Infrastructure.Lifecycle;

namespace Morris.Roslynk.Tests.Features.DeadCode.FindDeadCodeTests;

public class FindDeadCodeTests
{
	[Fact]
	public async Task WhenAPrivateMethodIsNeverCalled_ThenItIsReportedWithHighConfidence()
	{
		string result = await RunAsync();

		Assert.DoesNotContain("#error=", result);
		Assert.Contains("SimpleLibrary.Widget.Unused", DeadLeaves(result));
		// The leaf carries the declaration range then the confidence: 'method,Unused,<loc>,High ...'.
		Assert.Matches(new Regex(@"method,Unused,\d+:\d+-\d+:\d+,High"), result);
	}

	[Fact]
	public async Task WhenAMethodIsReferencedOrImplementsAnInterface_ThenItIsNotReported()
	{
		string result = await RunAsync(includePublic: true);

		Assert.DoesNotContain("SimpleLibrary.Widget.Compute", DeadLeaves(result));
		Assert.DoesNotContain("SimpleLibrary.Greeter.Greet", DeadLeaves(result));
	}

	[Fact]
	public async Task WhenIncludePublicIsFalse_ThenUnreferencedPublicMembersAreNotReported()
	{
		string result = await RunAsync(includePublic: false);

		Assert.DoesNotContain("SimpleLibrary.Caller.Run", DeadLeaves(result));
	}

	[Fact]
	public async Task WhenIncludePublicIsTrue_ThenUnreferencedPublicMembersAreReported()
	{
		string result = await RunAsync(includePublic: true);

		Assert.Contains("SimpleLibrary.Caller.Run", DeadLeaves(result));
	}

	[Fact]
	public async Task WhenTheBodyNestsByFileNamespaceAndType_ThenTheMemberSitsUnderItsContainingType()
	{
		string result = await RunAsync();

		Assert.Contains("\tSimpleLibrary\n", result);
		Assert.Contains("\t\tclass,Widget\n", result);
		Assert.Contains("\t\t\tmethod,Unused,", result);
	}

	[Fact]
	public async Task WhenAScopeIsGiven_ThenOnlyMatchingSymbolsAreConsidered()
	{
		string result = await RunAsync(scope: "SimpleLibrary.Widget");

		IReadOnlyList<string> leaves = DeadLeaves(result);
		Assert.NotEmpty(leaves);
		Assert.All(leaves, leaf => Assert.StartsWith("SimpleLibrary.Widget", leaf));
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

	/// <summary>
	/// Reconstructs the fully-qualified name of each reported (leaf) candidate from the nested
	/// file -> namespace -> type -> member outline. A leaf line is 'kind,name,&lt;loc&gt;,&lt;confidence&gt; reason'
	/// (four comma fields, the fourth starting with the confidence); a namespace line has no comma and a
	/// parent type line is just 'kind,name'.
	/// </summary>
	private static IReadOnlyList<string> DeadLeaves(string text)
	{
		var names = new Dictionary<int, string>();
		var leaves = new List<string>();

		foreach (string line in text.Split('\n'))
		{
			if (line.Length == 0 || line[0] == '#')
				continue;

			int depth = 0;
			while (depth < line.Length && line[depth] == '\t')
				depth++;

			if (depth == 0)
			{
				names.Clear();
				continue;
			}

			string content = line[depth..];
			string[] parts = content.Split(',');
			names[depth] = parts.Length == 1 ? content : parts[1];
			foreach (int deeper in names.Keys.Where(key => key > depth).ToList())
				names.Remove(deeper);

			bool isLeaf = parts.Length >= 4
				&& (parts[3].StartsWith("High", StringComparison.Ordinal) || parts[3].StartsWith("Medium", StringComparison.Ordinal));
			if (!isLeaf)
				continue;

			var segments = new List<string>();
			for (int level = 1; level <= depth; level++)
				segments.Add(names[level]);

			leaves.Add(string.Join('.', segments));
		}

		return leaves;
	}
}
