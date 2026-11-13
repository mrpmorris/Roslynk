using Microsoft.Extensions.Logging;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class PluginLoaderTests : PluginLoaderHarness
{
	[Fact]
	public async Task WhenTheCandidateIsValid_ThenThePluginLoadsAndRegistersItsTools()
	{
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.ValidPluginSource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.NotNull(loaded);
		Assert.Equal("callgraph", loaded!.NormalizedName);
		Assert.Equal("CallGraph", loaded.Name);
		Assert.Equal("CallGraph.Roslynk.Plugin", loaded.FactoryType.Assembly.GetName().Name);
		Assert.Empty(Sink.Added);
		Assert.Single(Logger.Records, record => record.Level == LogLevel.Information);
	}

	[Fact]
	public async Task WhenThePluginShipsItsOwnContractCopy_ThenTheHostsCopyWins()
	{
		// A stale contract DLL deployed beside the plugin must never be loaded: the host's own copy
		// is what gives IPluginContext and IPlugin their identity. Success proves the identity held —
		// a second contract copy would make the factory's IPluginContext parameter binding fail.
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.ValidPluginSource);
		File.Copy(
			typeof(IPluginContext).Assembly.Location,
			Path.Combine(candidate.PluginFolderPath, "Morris.Roslynk.Plugins.Abstractions.dll"));

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.NotNull(loaded);
	}

	[Fact]
	public async Task WhenThePluginReferencesCodeAnalysis_ThenItBindsTheHostsCopyRegardlessOfVersion()
	{
		PluginCandidate candidate = Stage("CodeProbe", TestPluginCompiler.CodeAnalysisProbeSource, roslynReference: true);

		await LoadAsync(candidate);

		(string? Stage, string Message, Exception? Exception) error = SingleError();
		// The factory failed on purpose, carrying the identity of the assembly its Solution type
		// came from; that must be the host's own copy, not a second one loaded from the plugin folder.
		Assert.Equal("factory-failure", error.Stage);
		Assert.Contains(typeof(Microsoft.CodeAnalysis.Solution).Assembly.FullName!, error.Message);
	}

	[Fact]
	public async Task WhenTheFactoryAttributeIsMissing_ThenItIsReportedAsFactoryTypeInvalid()
	{
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.MissingAttributeSource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("factory-type-invalid", error.Stage);
		Assert.Contains("carries no [assembly: PluginFactory<...>]", error.Message);
		Assert.Contains("entryAssembly=", error.Message);
		Assert.Contains("(attribute not read)", error.Message);
	}

	[Fact]
	public async Task WhenTwoFactoryAttributesAreApplied_ThenItIsReportedAsFactoryTypeInvalid()
	{
		// The real compiler refuses two applications outright (duplicate attribute); the loader's
		// branch is the cushion for an assembly that never ran that check, staged with the stub
		// contract whose attribute allows two applications.
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.TwoAttributesSource, extraAssemblies:
			[("Morris.Roslynk.Plugins.Abstractions", TestPluginCompiler.StubContractAllowingTwoClaimsSource)]);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("factory-type-invalid", error.Stage);
		Assert.Contains("2 [assembly: PluginFactory<...>] applications", error.Message);
	}

	[Fact]
	public async Task WhenTheFactoryTypeDoesNotImplementIPluginFactory_ThenTheGuardReportsFactoryTypeInvalid()
	{
		// The stub attribute carries no generic constraint, so a claim the compiler would reject still
		// builds. At load time the claim binds to the host's real contract, whose constraint the factory
		// type violates: reading the attribute itself fails with a TypeLoadException naming the violated
		// constraint, and the loader reports it as factory-type-invalid — the same stage the assignability
		// guard would produce for an assembly whose claim could be read.
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.ForeignFactoryClaimSource, extraAssemblies:
			[("Morris.Roslynk.Plugins.Abstractions", TestPluginCompiler.StubContractSource)]);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("factory-type-invalid", error.Stage);
		Assert.Contains("violates the constraint", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WhenTheFactoryIsAnExplicitInterfaceImplementation_ThenTheShimDispatchesIt()
	{
		// Explicit interface implementations are metadata-named IPluginFactory.Create, invisible to a
		// name-based GetMethod; the generic shim binds them anyway.
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.ExplicitImplementationSource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.NotNull(loaded);
	}

	[Fact]
	public async Task WhenTheFactoryClassIsInternal_ThenTheShimDispatchesIt()
	{
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.InternalFactorySource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.NotNull(loaded);
	}

	[Fact]
	public async Task WhenTheFactoryReturnsNull_ThenItIsThePluginSOwnInitializationFailureWithNoStage()
	{
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.NullReturnSource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Null(error.Stage);
		Assert.Contains("returned null", error.Message);
		Assert.Contains("next daemon start invokes the factory again", error.Message);
	}

	[Fact]
	public async Task WhenTheFactoryThrows_ThenTheUnderlyingExceptionIsReportedNotTheReflectionWrapper()
	{
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.ThrowingFactorySource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("factory-failure", error.Stage);
		var inner = Assert.IsType<InvalidOperationException>(error.Exception);
		Assert.Equal("the plugin's own initialization failed", inner.Message);
	}

	[Fact]
	public async Task WhenAPrivateDependencyIsMissing_ThenTheLoadFailsAsDependencyUnresolvedNamingIt()
	{
		// SomeLibrary is referenced at build time but never deployed beside the plugin.
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.MissingDependencySource, extraAssemblies:
			[("SomeLibrary", TestPluginCompiler.MissingDependencyLibrarySource)]);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("dependency-unresolved", error.Stage);
		Assert.Contains("SomeLibrary", error.Message);
	}

	[Fact]
	public async Task WhenTheFactoryNeedsAMissingDependencyOnlyAtInvocation_ThenItIsReportedAsDependencyUnresolved()
	{
		// The claim and the factory's shape are clean — the dependency is first touched when the
		// factory's body runs. The spec maps any unsatisfiable reference to dependency-unresolved,
		// naming the missing assembly, wherever the bind fails.
		PluginCandidate candidate = Stage("CallGraph", TestPluginCompiler.MissingDependencyAtCallSource, extraAssemblies:
			[("SomeLibrary", TestPluginCompiler.MissingDependencyLibrarySource)]);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("dependency-unresolved", error.Stage);
		Assert.Contains("SomeLibrary", error.Message);
	}

	[Fact]
	public async Task WhenTheStartupBudgetIsExhausted_ThenThePluginIsRecordedAsStartupTimeoutAndNothingRegisters()
	{
		// The budget is a stopwatch, not a kill switch: the sleeping factory runs to completion
		// (about 400ms against a 200ms budget), then the next checkpoint skips every remaining stage
		// and rolls back whatever was registered.
		StartupBudget = TimeSpan.FromMilliseconds(200);
		PluginCandidate candidate = Stage("Slow", TestPluginCompiler.SlowFactorySource);

		LoadedPlugin? loaded = await LoadAsync(candidate);

		Assert.Null(loaded);
		(string? Stage, string Message, Exception? Exception) error = SingleError();
		Assert.Equal("startup-timeout", error.Stage);
		Assert.Contains("factory invocation", error.Message);
		Assert.Contains("retried on the next daemon start", error.Message);
		Assert.Empty(Sink.Added);
	}
}
