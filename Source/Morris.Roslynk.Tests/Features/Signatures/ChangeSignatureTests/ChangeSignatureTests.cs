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

		Assert.Contains("#applied=true", result);
		Assert.Contains("#updatedCallSites=1", result);
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

		Assert.Contains("#applied=false", result);
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

		Assert.Contains("#error=NotSupported", result);
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

		Assert.Contains("#error=Invalid", result);
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

		Assert.Contains("#error=NotFound", result);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Widget.Compute", "int", "factor", "1");

		Assert.Contains("#error=Indexing", result);
		Assert.Contains("#status=Building", result);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
