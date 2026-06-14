using Morris.Roslynk.Features.Symbols.GetMethod;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetMethodTests;

public class GetMethodTests
{
	[Fact]
	public async Task WhenAMethodIsRequested_ThenItsSignatureParametersAndDocsAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResponse response = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.Widget.Compute");

		MethodDto method = Assert.Single(response.Methods);
		Assert.Equal("int", method.ReturnType);
		ParameterDto parameter = Assert.Single(method.Parameters);
		Assert.Equal("value", parameter.Name);
		Assert.Equal("int", parameter.Type);
		Assert.Equal("own", method.Documentation.Source);
	}

	[Fact]
	public async Task WhenTheNameResolvesToAType_ThenNoMethodsAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResponse response = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.Widget");

		Assert.Empty(response.Methods);
	}

	[Fact]
	public async Task WhenTheMethodIsNotFound_ThenMethodsAndCandidatesAreEmpty()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResponse response = await subject.GetMethod(TestSolutions.Simple, "SimpleLibrary.DoesNotExist");

		Assert.Empty(response.Methods);
		Assert.Empty(response.Candidates);
	}

	[Fact]
	public async Task WhenAMetadataMethodIsRequested_ThenItsOverloadsResolveFromTheReferencedAssembly()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMethodTool(registry, new SymbolResolver());

		GetMethodResponse response = await subject.GetMethod(TestSolutions.Simple, "System.String.Substring");

		Assert.NotEmpty(response.Methods);
		Assert.All(response.Methods, method => Assert.Equal("Substring", method.Name));
	}
}
