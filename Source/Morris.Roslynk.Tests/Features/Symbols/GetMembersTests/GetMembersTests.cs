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

	[Fact]
	public async Task WhenANameFilterEndsWithAStar_ThenOnlyPrefixMatchesAreReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String", nameFilter: "Sub*");

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Members!, member => member.Name == "Substring");
		Assert.All(result.Members!, member => Assert.StartsWith("Sub", member.Name, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenANameFilterHasNoStar_ThenItMatchesAsACaseInsensitiveSubstring()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String", nameFilter: "ubSTR");

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Members!, member => member.Name == "Substring");
		Assert.All(result.Members!, member => Assert.Contains("ubstr", member.Name, StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task WhenMethodsAreExcluded_ThenNoMethodsAreReturnedButOtherKindsRemain()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String", includeMethods: false);

		Assert.True(result.IsSuccess);
		Assert.DoesNotContain(result.Members!, member => member.Kind == "Method");
		Assert.Contains(result.Members!, member => member.Name == "Length");
	}

	[Fact]
	public async Task WhenOnlyFieldsAreRequested_ThenOtherKindsAreExcluded()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(
			TestSolutions.Simple,
			"System.String",
			includeMethods: false,
			includeProperties: false,
			includeEvents: false,
			includeNestedTypes: false);

		Assert.True(result.IsSuccess);
		Assert.NotEmpty(result.Members!);
		Assert.All(result.Members!, member => Assert.Equal("Field", member.Kind));
		Assert.Contains(result.Members!, member => member.Name == "Empty");
	}

	[Fact]
	public async Task WhenANameFilterMatchesButItsKindIsExcluded_ThenItIsNotReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String", nameFilter: "Length", includeProperties: false);

		Assert.True(result.IsSuccess);
		Assert.DoesNotContain(result.Members!, member => member.Name == "Length");
	}

	[Fact]
	public async Task WhenNoFiltersAreGiven_ThenMethodsAndPropertiesAreBothReturned()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String");

		Assert.True(result.IsSuccess);
		Assert.Contains(result.Members!, member => member.Kind == "Method");
		Assert.Contains(result.Members!, member => member.Kind == "Property");
	}

	[Fact]
	public async Task WhenAMemberHasSource_ThenItsSourceLocationIsReturnedForReadingTheBody()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "SimpleLibrary.Greeter", nameFilter: "Greet");

		Assert.True(result.IsSuccess);
		MemberDto member = Assert.Single(result.Members!, candidate => candidate.Name == "Greet");
		Assert.False(string.IsNullOrEmpty(member.SourcePath));
		Assert.False(Path.IsPathRooted(member.SourcePath), $"expected a solution-relative path, got '{member.SourcePath}'");
		Assert.EndsWith("Greeter.cs", member.SourcePath!);
		Assert.NotNull(member.StartLine);
		Assert.NotNull(member.EndLine);
		Assert.True(member.EndLine >= member.StartLine);
	}

	[Fact]
	public async Task WhenAMemberComesFromMetadata_ThenItHasNoSourceLocation()
	{
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(TestSolutions.Simple);
		var subject = new GetMembersTool(registry, new SymbolResolver());

		GetMembersResult result = await subject.GetMembers(TestSolutions.Simple, "System.String", nameFilter: "Substring");

		Assert.True(result.IsSuccess);
		Assert.NotEmpty(result.Members!);
		Assert.All(result.Members!, member => Assert.Null(member.SourcePath));
	}
}
