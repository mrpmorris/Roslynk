using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Tests.Features.Symbols.GetMembersTests;

public class GetMembersTests
{
	[Fact]
	public async Task WhenATypesMembersAreRequested_ThenItsPublicMethodsAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.True(result.IsSuccess);
		Assert.Equal("SimpleLibrary.Greeter", result.ResolvedType);
		Assert.Contains(result.Members!, member => member.Name == "Greet");
	}

	[Fact]
	public async Task WhenAMetadataTypesMembersAreRequested_ThenTheyResolveFromTheReferencedAssembly()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String");

		Assert.True(result.IsSuccess);
		Assert.Equal("System.String", result.ResolvedType);
		Assert.Contains(result.Members!, member => member.Name == "Substring");
	}

	[Fact]
	public async Task WhenTheSolutionIsStillLoading_ThenIndexingIsReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.False(result.IsSuccess);
		Assert.Equal(ErrorCode.Indexing, result.Error!.Code);
		Assert.Equal(SolutionStatus.Building, result.Status);

		await registry.GetOrAddAsync(TestSolutions.Simple);
	}
}
