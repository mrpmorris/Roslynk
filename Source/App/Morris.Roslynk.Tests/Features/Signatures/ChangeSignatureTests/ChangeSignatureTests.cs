using Morris.Roslynk.Features.Signatures.ChangeSignature;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.Signatures.ChangeSignatureTests;

public class ChangeSignatureTests
{
	[Fact]
	public async Task WhenAddingAParameterWithACallSiteValue_ThenTheMethodAndItsCallsAreUpdated()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			solutionPath, "SimpleLibrary.Widget.Compute", "int", "factor", "1", callSiteArgument: "1");

		Assert.Contains("applied=Y", result);
		Assert.Contains("updatedCallSites=1", result);
		string text = await File.ReadAllTextAsync(FindFile(solutionPath, "Widget.cs"));
		Assert.Contains("int factor = 1", text);
		Assert.Contains("factor: 1", text);
	}

	[Fact]
	public async Task WhenAddingAParameterWithCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());
		string widget = FindFile(solutionPath, "Widget.cs");
		string before = await File.ReadAllTextAsync(widget);

		string result = await subject.ChangeSignature(
			solutionPath, "SimpleLibrary.Widget.Compute", "int", "factor", "1", callSiteArgument: "1", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains("Widget.cs", result);
		Assert.Equal(before, await File.ReadAllTextAsync(widget));
	}

	[Fact]
	public async Task WhenTheMethodImplementsAnInterfaceMember_ThenItIsNotSupported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Greeter.Greet", "int", "times", "1");

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("interface", result, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task WhenNoDefaultValueIsGiven_ThenItIsRefused()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Widget.Compute", "int", "factor", "");

		Assert.Contains("error=Invalid", result);
		Assert.Contains("default", result, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.DoesNotExist", "int", "factor", "1");

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Widget.Compute", "int", "factor", "1");

		Assert.Contains("error=Indexing", result);
		Assert.Contains("status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	[Fact]
	public async Task WhenAnOverloadIsTargetedBySignature_ThenThatOverloadIsChanged()
	{
		string solutionPath = TestSolutions.CreateScratchSimpleSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			solutionPath, "SimpleLibrary.Ledger.Add(int, int)", "int", "step", "1", callSiteArgument: "1");

		Assert.Contains("applied=Y", result);
		string text = await File.ReadAllTextAsync(FindFile(solutionPath, "Ledger.cs"));
		Assert.Contains("public int Add(int amount, int times, int step = 1)", text);
		Assert.Contains("public int Add(int amount)", text);
	}

	[Fact]
	public async Task WhenTheMethodIsDeclaredInARazorCodeBlock_ThenTheRazorFileIsRewrittenOnDisk()
	{
		string solutionPath = await CreateRazorSolutionWithLabelAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			solutionPath, "RazorLib.Counter.Label", "string", "prefix", "\"#\"", callSiteArgument: "\"x\"");

		Assert.Contains("applied=Y", result);
		Assert.Contains("updatedCallSites=2", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");
		string counter = await File.ReadAllTextAsync(FindFile(solutionPath, "Counter.razor"));
		Assert.Contains("private string Label(int value, string prefix = \"#\")", counter);
		Assert.Contains("@Label(1, prefix: \"x\")", counter);
		Assert.Contains("Label(2, prefix: \"x\");", counter);
	}

	[Fact]
	public async Task WhenTheRazorMethodIsChangedWithCheckOnly_ThenTheRazorFileIsListedAndNothingIsWritten()
	{
		string solutionPath = await CreateRazorSolutionWithLabelAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());
		string counterPath = FindFile(solutionPath, "Counter.razor");
		string before = await File.ReadAllTextAsync(counterPath);

		string result = await subject.ChangeSignature(
			solutionPath, "RazorLib.Counter.Label", "string", "prefix", "\"#\"", checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains(result.Split('\n'), line => line.TrimStart('\t') == "Counter.razor");
		Assert.Equal(before, await File.ReadAllTextAsync(counterPath));
	}

	/// <summary>A scratch Razor solution whose Counter declares Label, called from markup and from the @code block.</summary>
	private static async Task<string> CreateRazorSolutionWithLabelAsync()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = FindFile(solutionPath, "Counter.razor");
		string counter = await File.ReadAllTextAsync(counterPath);
		counter = counter
			.Replace("<p>Starting from @StartAt</p>", "<p>Starting from @StartAt</p>\r\n<p>@Label(1)</p>")
			.Replace("\tprivate void IncrementCount()", "\tprivate string Label(int value) => $\"#{value}\";\r\n\r\n\tprivate void Reset()\r\n\t{\r\n\t\tLabel(2);\r\n\t}\r\n\r\n\tprivate void IncrementCount()");
		await File.WriteAllTextAsync(counterPath, counter);
		return solutionPath;
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
