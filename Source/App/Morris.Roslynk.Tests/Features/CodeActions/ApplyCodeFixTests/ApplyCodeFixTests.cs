using Morris.Roslynk.Features.CodeActions.ApplyCodeAction;
using Morris.Roslynk.Features.CodeActions.ApplyCodeFix;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;
using Morris.Roslynk.Tests.Helpers;

namespace Morris.Roslynk.Tests.Features.CodeActions.ApplyCodeFixTests;

public class ApplyCodeFixTests
{
	/// <summary>The 1-based column of <c>unused</c> in UnusedLocalScenario's <c>int unused = 0;</c> line (indented by two tabs).</summary>
	private const int UnusedColumn = 7;

	[Fact]
	public async Task WhenFixingAnAnalyzerDiagnosticById_ThenTheFileIsUpdated()
	{
		// The point of issue #9: IDE0005 is listed by get_diagnostics, so it must be fixable here too.
		string solutionPath = UnnecessaryUsingScenario.Create(out string greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "IDE0005", UnnecessaryUsingScenario.UsingLine, 1);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain(UnnecessaryUsingScenario.UnnecessaryUsing, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenFixingAnAnalyzerDiagnosticCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = UnnecessaryUsingScenario.Create(out string greeter);
		string before = await File.ReadAllTextAsync(greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "IDE0005", UnnecessaryUsingScenario.UsingLine, 1, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Greeter.cs", result);
		Assert.Equal(before, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenFixingADiagnosticById_ThenTheFileIsUpdated()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219", unusedLine, UnusedColumn);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("int unused", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenNoSuchDiagnosticExists_ThenNotFoundIsReturned()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS9999", unusedLine, UnusedColumn);

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(TestSolutions.Simple, "Widget.cs", "CS0219", 1, 1);

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenTheFileHasSeveralOccurrences_ThenOnlyTheOneAtThePositionIsFixed()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		string content = (await File.ReadAllTextAsync(greeter)).Replace("\t\tint unused = 0;\r\n", "\t\tint unused = 0;\r\n\t\tint spare = 0;\r\n");
		await File.WriteAllTextAsync(greeter, content);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219", unusedLine + 1, UnusedColumn);

		Assert.True(result.Contains("applied=Y"), result);
		string after = await File.ReadAllTextAsync(greeter);
		Assert.Contains("int unused = 0;", after);
		Assert.DoesNotContain("int spare", after);
	}

	[Fact]
	public async Task WhenThePositionIsInsideTheDiagnosticSpan_ThenItIsFixed()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219", unusedLine, UnusedColumn + "unused".Length);

		Assert.True(result.Contains("applied=Y"), result);
		Assert.DoesNotContain("int unused", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenNoDiagnosticIsAtThePosition_ThenNotFoundIsReturnedAndNothingIsWritten()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		string before = await File.ReadAllTextAsync(greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219", unusedLine - 2, 1);

		Assert.Contains("error=NotFound", result);
		Assert.Contains($"{unusedLine - 2}:1", result);
		Assert.Equal(before, await File.ReadAllTextAsync(greeter));
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 0)]
	public async Task WhenThePositionIsNotOneBased_ThenInvalidIsReturned(int line, int column)
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out _);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0219", line, column);

		Assert.Contains("error=Invalid", result);
	}

	[Fact]
	public async Task WhenTheDiagnosticHasSeveralFixes_ThenConflictListsEachOnceAndNothingIsWritten()
	{
		string solutionPath = CreateUnimplementedInterface(out string greeter, out int line, out int column);
		string before = await File.ReadAllTextAsync(greeter);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0535", line, column);

		Assert.Contains("error=Conflict", result);
		Assert.Contains("apply_code_action", result);
		string[] candidates = Candidates(result);
		// Each member missing from the interface reports its own CS0535 offering the same two fixes.
		Assert.Equal(2, candidates.Length);
		Assert.Contains(candidates, candidate => candidate.EndsWith(",Fix,CS0535 Implement interface", StringComparison.Ordinal));
		Assert.Contains(candidates, candidate => candidate.EndsWith(",Fix,CS0535 Implement all members explicitly", StringComparison.Ordinal));
		Assert.Equal(before, await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenACandidateIsPassedToApplyCodeAction_ThenThatFixIsApplied()
	{
		string solutionPath = CreateUnimplementedInterface(out string greeter, out int line, out int column);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		CodeActionService service = TestServices.CodeActions();
		string conflict = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, greeter, "CS0535", line, column);
		string candidate = Candidates(conflict).Single(entry => entry.EndsWith(" Implement all members explicitly", StringComparison.Ordinal));
		string actionId = candidate[..candidate.IndexOf(',')];

		string result = await new ApplyCodeActionTool(registry, service, new ApplyPipeline()).ApplyCodeAction(solutionPath, actionId);

		Assert.True(result.Contains("applied=Y"), result);
		Assert.Contains("void IThing.Run()", await File.ReadAllTextAsync(greeter));
	}

	[Fact]
	public async Task WhenAFixIsGroupedUnderAParentAction_ThenTheNestedFixesAreCandidates()
	{
		string solutionPath = UnusedLocalScenario.Create(out string greeter, out int unusedLine);
		string content = (await File.ReadAllTextAsync(greeter)).Replace("\t\tint unused = 0;", "\t\tStringBuilder unused = null;");
		await File.WriteAllTextAsync(greeter, content);
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = TestServices.ApplyCodeFix(registry);

		string result = await subject.ApplyCodeFix(solutionPath, greeter, "CS0246", unusedLine, 3);

		Assert.Contains("error=Conflict", result);
		string[] candidates = Candidates(result);
		Assert.Contains(candidates, candidate => candidate.EndsWith(",Fix,CS0246 using System.Text;", StringComparison.Ordinal));
		Assert.Contains(candidates, candidate => candidate.EndsWith(",Fix,CS0246 Generate class 'StringBuilder'", StringComparison.Ordinal));
	}

	private static string[] Candidates(string result) =>
		result.Split('\n')
			.Where(entry => entry.StartsWith("candidate=", StringComparison.Ordinal))
			.Select(entry => entry["candidate=".Length..].TrimEnd('\r'))
			.ToArray();

	/// <summary>A scratch SimpleSolution whose Greeter.cs declares a class that implements none of its interface's members (CS0535).</summary>
	private static string CreateUnimplementedInterface(out string greeterPath, out int line, out int column)
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		greeterPath = Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, "Greeter.cs", SearchOption.AllDirectories).First();

		const string content =
			"namespace SimpleLibrary;\r\n\r\n" +
			"public interface IThing\r\n{\r\n\tvoid Run();\r\n\tint Count { get; }\r\n}\r\n\r\n" +
			"public class Thing : IThing\r\n{\r\n}\r\n\r\n" +
			"public class Greeter : IGreeter\r\n{\r\n" +
			"\tpublic string Greet(string name) => $\"Hello, {name}!\";\r\n}\r\n";
		File.WriteAllText(greeterPath, content);

		int offset = content.IndexOf(": IThing", StringComparison.Ordinal) + 2;
		line = content[..offset].Count(character => character == '\n') + 1;
		column = offset - content.LastIndexOf('\n', offset - 1);
		return solutionPath;
	}
}
