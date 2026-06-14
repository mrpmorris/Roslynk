using Morris.Roslynk.Features.Signatures.ChangeSignature;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
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

		ChangeSignatureResult result = await subject.ChangeSignature(
			solutionPath, "SimpleLibrary.Widget.Compute", "int", "factor", "1", callSiteArgument: "1");

		Assert.True(result.IsSuccess);
		Assert.True(result.Applied);
		Assert.Equal(1, result.UpdatedCallSites);
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

		ChangeSignatureResult result = await subject.ChangeSignature(
			solutionPath, "SimpleLibrary.Widget.Compute", "int", "factor", "1", callSiteArgument: "1", checkOnly: true);

		Assert.True(result.IsSuccess);
		Assert.False(result.Applied);
		Assert.NotEmpty(result.ChangedFiles!);
		Assert.Equal(before, await File.ReadAllTextAsync(widget));
	}

	[Fact]
	public async Task WhenTheMethodImplementsAnInterfaceMember_ThenItIsNotSupported()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		ChangeSignatureResult result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Greeter.Greet", "int", "times", "1");

		Assert.Equal(ErrorCode.NotSupported, result.Error!.Code);
		Assert.Contains("interface", result.Error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task WhenNoDefaultValueIsGiven_ThenItIsRefused()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		ChangeSignatureResult result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Widget.Compute", "int", "factor", "");

		Assert.Equal(ErrorCode.Invalid, result.Error!.Code);
		Assert.Contains("default", result.Error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		ChangeSignatureResult result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.DoesNotExist", "int", "factor", "1");

		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
		Assert.Null(result.ResolvedMethod);
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		ChangeSignatureResult result = await subject.ChangeSignature(
			TestSolutions.Simple, "SimpleLibrary.Widget.Compute", "int", "factor", "1");

		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}

	private static string FindFile(string solutionPath, string fileName) =>
		Directory.EnumerateFiles(Path.GetDirectoryName(solutionPath)!, fileName, SearchOption.AllDirectories).First();
}
