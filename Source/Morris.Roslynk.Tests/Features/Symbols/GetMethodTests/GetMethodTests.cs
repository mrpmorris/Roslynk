using Morris.Roslynk.Features.Symbols.GetMethod;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.GetMethodTests;

public class GetMethodTests
{
	[Fact]
	public async Task WhenAMethodIsRequested_ThenItsSignatureParametersAndDocsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResult result = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.Widget.Compute");

		Assert.True(result.IsSuccess);
		Assert.Equal(SolutionStatus.Ready, result.Status);
		Assert.False(string.IsNullOrEmpty(result.SnapshotId));
		MethodDto method = Assert.Single(result.Methods!);
		Assert.Equal("int", method.ReturnType);
		ParameterDto parameter = Assert.Single(method.Parameters);
		Assert.Equal("value", parameter.Name);
		Assert.Equal("int", parameter.Type);
		Assert.Equal("own", method.Documentation.Source);
	}

	[Fact]
	public async Task WhenTheNameResolvesToAType_ThenNotFoundCarriesTheTypeAsACandidate()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResult result = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.Widget");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
		Assert.Contains("SimpleLibrary.Widget", result.Error.Candidates!);
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenNotFoundIsReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResult result = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.NotFound, result.Error!.Code);
	}

	[Fact]
	public async Task WhenAMetadataMethodIsRequested_ThenItsOverloadsResolveFromTheReferencedAssembly()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResult result = await subject.GetMethod(TestSolutions.Simple, "System.String.Substring");

		Assert.True(result.IsSuccess);
		Assert.NotEmpty(result.Methods!);
		Assert.All(result.Methods!, method => Assert.Equal("Substring", method.Name));
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResult result = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.Widget.Compute");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
