using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Plugins.Tests;

/// <summary>
/// Asserts the shipped contract assembly's public surface and the structural versioning invariant:
/// the assembly name, its major version and the stamped ApiVersion all say the same contract major.
/// </summary>
public class ContractSurfaceTests
{
	private static Assembly ContractAssembly { get; } = typeof(IPluginContext).Assembly;

	[Fact]
	public void WhenTheContractAssemblyIsBuilt_ThenItsNameAndMajorVersionAgree()
	{
		Assert.Equal("Morris.Roslynk.Plugins.V1", ContractAssembly.GetName().Name);
		Assert.Equal(1, ContractAssembly.GetName().Version!.Major);
	}

	[Fact]
	public void WhenStampingTheApiVersion_ThenTheAttributeCarriesIt()
	{
		var attribute = new RoslynkApiVersionAttribute(ApiVersion.V1);

		Assert.Equal(ApiVersion.V1, attribute.Version);
	}

	[Fact]
	public void WhenInspectingIPluginContext_ThenItExposesLoggingTheBudgetAndSolutionAccess()
	{
		Type type = typeof(IPluginContext);

		Assert.Equal(typeof(ILoggerFactory), type.GetProperty("LoggerFactory")!.PropertyType);
		Assert.Equal(typeof(int), type.GetProperty("MaxResponseChars")!.PropertyType);

		MethodInfo useSolutionAsync = type.GetMethod("UseSolutionAsync")!;
		Assert.True(useSolutionAsync.IsGenericMethod);
		Assert.Equal("solutionId", useSolutionAsync.GetParameters()[0].Name);
		Assert.Equal(typeof(string), useSolutionAsync.GetParameters()[0].ParameterType);
		Assert.Equal(typeof(CancellationToken), useSolutionAsync.GetParameters()[2].ParameterType);
	}

	[Fact]
	public void WhenInspectingISolutionContext_ThenItExposesTheSnapshotAndDiagnostics()
	{
		Type type = typeof(ISolutionContext);

		Assert.Equal(typeof(Solution), type.GetProperty("Solution")!.PropertyType);

		MethodInfo getDiagnostics = type.GetMethod("GetDiagnosticsAsync")!;
		Assert.Equal(typeof(ValueTask<ImmutableArray<Diagnostic>>), getDiagnostics.ReturnType);
		Assert.Equal("cancellationToken", getDiagnostics.GetParameters()[0].Name);
	}

	[Fact]
	public void WhenInspectingISolutionEvent_ThenItExposesKindSolutionIdSolutionAndErrorMessage()
	{
		Type type = typeof(ISolutionEvent);

		Assert.NotNull(type.GetProperty("Kind"));
		Assert.NotNull(type.GetProperty("SolutionId"));
		Assert.Equal(typeof(Solution), type.GetProperty("Solution")!.PropertyType);
		Assert.Equal(typeof(string), type.GetProperty("ErrorMessage")!.PropertyType);
	}

	[Fact]
	public void WhenInspectingIRoslynkPlugin_ThenItIsAnObserverOfSolutionEvents()
	{
		Assert.True(typeof(IObserver<ISolutionEvent>).IsAssignableFrom(typeof(IRoslynkPlugin)));
	}

	[Fact]
	public void WhenInspectingTheEnums_ThenTheyCarryTheSpecifiedMembers()
	{
		Assert.Equal([ApiVersion.V1], Enum.GetValues<ApiVersion>());

		Assert.Equal(
			[SolutionEventKind.Building, SolutionEventKind.Ready, SolutionEventKind.Failed, SolutionEventKind.Evicted],
			Enum.GetValues<SolutionEventKind>());

		Assert.Equal(
			[UseSolutionResultState.Success, UseSolutionResultState.NotFound, UseSolutionResultState.Faulted],
			Enum.GetValues<UseSolutionResultState>());
	}
}
