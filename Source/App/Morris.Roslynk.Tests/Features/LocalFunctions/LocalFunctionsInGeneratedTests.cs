using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Lifecycle;
using LocalFunctionsClass = Morris.Roslynk.Infrastructure.Resolution.LocalFunctions;

namespace Morris.Roslynk.Tests.Features.LocalFunctions;

public class LocalFunctionsInGeneratedTests
{
	[Fact]
	public async Task WhenALocalFunctionIsDeclaredInGeneratedCode_ThenFindAllFindsIt()
	{
		// Describe is emitted into Shape.g.cs; Documents never lists it, so the walk must ask for the
		// project's source-generated documents too.
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);

		IReadOnlyList<IMethodSymbol> found = await LocalFunctionsClass.FindAllAsync(instance.CurrentSolution, name => name == "Format");

		IMethodSymbol local = Assert.Single(found);
		Assert.Equal("Describe", local.ContainingSymbol.Name);
		Assert.Contains("Shape.g.cs", local.DeclaringSyntaxReferences.Single().SyntaxTree.FilePath, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task WhenNoGeneratedLocalFunctionMatches_ThenFindAllReturnsNothingFromThem()
	{
		using var registry = new InstanceRegistry();
		RoslynInstance instance = await registry.GetOrAddAsync(TestSolutions.Generator);

		IReadOnlyList<IMethodSymbol> found = await LocalFunctionsClass.FindAllAsync(instance.CurrentSolution, name => name == "NoSuchLocalFunction");

		Assert.Empty(found);
	}
}
