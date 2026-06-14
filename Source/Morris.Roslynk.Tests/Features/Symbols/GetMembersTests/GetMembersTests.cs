using Morris.Roslynk.Features.Symbols.GetMembers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

namespace Morris.Roslynk.Tests.Features.Symbols.GetMembersTests;

public class GetMembersTests
{
	[Fact]
	public async Task WhenATypesMembersAreRequested_ThenItsPublicMethodsAreReturned()
	{
		using var registry = new InstanceRegistry();
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResponse response = await subject.GetMembers(TestSolutions.Simple, "SimpleLibrary.Greeter");

		Assert.Equal("SimpleLibrary.Greeter", response.ResolvedType);
		Assert.Contains(response.Members, member => member.Name == "Greet");
	}
}
