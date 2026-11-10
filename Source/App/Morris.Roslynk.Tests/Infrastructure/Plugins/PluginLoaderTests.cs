using System.Reflection;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class PluginLoaderTests : IDisposable
{
	private readonly string Directory;

	public PluginLoaderTests()
	{
		Directory = Path.Combine(Path.GetTempPath(), "roslynk-loader-tests-" + Guid.NewGuid().ToString("N"));
		_ = System.IO.Directory.CreateDirectory(Directory);
	}

	public void Dispose()
	{
		try
		{
			System.IO.Directory.Delete(Directory, recursive: true);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// The non-collectible load context keeps the entry DLL mapped until the process exits,
			// so a folder with a loaded plugin cannot be deleted from within the test run.
		}
	}

	[Fact]
	public void WhenTheEntryAssemblyLoads_ThenTheResultCarriesItAndAContextThatIsNeverCollected()
	{
		string pluginDirectory = StagePlugin(TestPluginCompiler.ApiVersionV1Source, contractReference: true);

		PluginLoadResult result = new PluginLoader().Load(Candidate(pluginDirectory));

		Assert.True(result.IsSuccess, result.Reason);
		Assert.NotNull(result.Assembly);
		Assert.False(result.LoadContext!.IsCollectible);
	}

	[Fact]
	public void WhenThePluginShipsItsOwnContractCopy_ThenTheHostsCopyWins()
	{
		// A stale contract DLL deployed beside the plugin must never be loaded: the host's own copy
		// is what gives IPluginContext and IRoslynkPlugin their identity.
		string pluginDirectory = StagePlugin(
			"""
			[assembly: Morris.Roslynk.Plugins.V1.RoslynkApiVersion(Morris.Roslynk.Plugins.V1.ApiVersion.V1)]

			internal static class Probe
			{
				internal static readonly Morris.Roslynk.Plugins.V1.ApiVersion? Version;
			}
			""",
			contractReference: true);
		TestPluginCompiler.Emit(
			Path.Combine(pluginDirectory, "Morris.Roslynk.Plugins.V1.dll"),
			"""
			namespace Morris.Roslynk.Plugins.V1
			{
				public class OnlyInFakeContract { }
			}
			""",
			contractReference: false,
			assemblyName: "Morris.Roslynk.Plugins.V1");

		PluginLoadResult result = new PluginLoader().Load(Candidate(pluginDirectory));

		Assert.True(result.IsSuccess, result.Reason);
		FieldInfo field = result.Assembly!.GetType("Probe")!.GetField("Version", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
		Type fieldType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
		Assert.Same(typeof(ApiVersion).Assembly, fieldType.Assembly);
	}

	[Fact]
	public void WhenADependencyIsNotInTheHost_ThenItIsLoadedFromThePluginFolder()
	{
		string pluginDirectory = Path.Combine(Directory, "CallGraph");
		_ = System.IO.Directory.CreateDirectory(pluginDirectory);
		string someLibPath = Path.Combine(pluginDirectory, "SomeLib.dll");
		TestPluginCompiler.Emit(
			someLibPath,
			"""
			namespace SomeLib
			{
				public class Helper { }
			}
			""",
			contractReference: false,
			assemblyName: "SomeLib");
		TestPluginCompiler.Emit(
			Path.Combine(pluginDirectory, "CallGraph.Roslynk.Plugin.dll"),
			"""
			[assembly: Morris.Roslynk.Plugins.V1.RoslynkApiVersion(Morris.Roslynk.Plugins.V1.ApiVersion.V1)]

			internal static class Probe
			{
				internal static readonly SomeLib.Helper? Helper;
			}
			""",
			contractReference: true,
			extraReferencePaths: [someLibPath]);

		PluginLoadResult result = new PluginLoader().Load(Candidate(pluginDirectory));

		Assert.True(result.IsSuccess, result.Reason);
		FieldInfo field = result.Assembly!.GetType("Probe")!.GetField("Helper", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
		Type fieldType = Nullable.GetUnderlyingType(field.FieldType) ?? field.FieldType;
		Assert.True(
			fieldType.Assembly.Location.StartsWith(pluginDirectory, StringComparison.OrdinalIgnoreCase),
			$"SomeLib resolved to {fieldType.Assembly.Location}");
	}

	[Fact]
	public void WhenTheFactoryIsValid_ThenItIsFound()
	{
		string pluginDirectory = StagePlugin(
			TestPluginCompiler.ApiVersionV1Source + """

			public static class Factories
			{
				[Morris.Roslynk.Plugins.V1.RoslynkPluginFactory]
				public static Morris.Roslynk.Plugins.V1.IRoslynkPlugin? Create(Morris.Roslynk.Plugins.V1.IPluginContext context) => null;
			}
			""",
			contractReference: true);
		PluginLoader loader = new();

		PluginLoadResult loadResult = loader.Load(Candidate(pluginDirectory));
		PluginFactoryResult factoryResult = loader.FindFactory(loadResult);

		Assert.True(factoryResult.IsSuccess, factoryResult.Reason);
		Assert.Equal("Create", factoryResult.FactoryMethod!.Name);
	}

	[Fact]
	public void WhenNoFactoryIsDeclared_ThenItIsReportedAsAFailure()
	{
		string pluginDirectory = StagePlugin(TestPluginCompiler.ApiVersionV1Source, contractReference: true);
		PluginLoader loader = new();

		PluginLoadResult loadResult = loader.Load(Candidate(pluginDirectory));
		PluginFactoryResult factoryResult = loader.FindFactory(loadResult);

		Assert.False(factoryResult.IsSuccess);
		Assert.Equal(PluginDiscoveryStage.FactoryFailure, factoryResult.Stage);
	}

	[Fact]
	public void WhenTheTypeDeclaringTheFactoryCannotLoad_ThenTheMissingDependencyIsReported()
	{
		string pluginDirectory = Path.Combine(Directory, "CallGraph");
		_ = System.IO.Directory.CreateDirectory(pluginDirectory);
		string missingLibPath = EmitMissingLib(pluginDirectory);
		TestPluginCompiler.Emit(
			Path.Combine(pluginDirectory, "CallGraph.Roslynk.Plugin.dll"),
			TestPluginCompiler.ApiVersionV1Source + """

			public static class Factories
			{
				[Morris.Roslynk.Plugins.V1.RoslynkPluginFactory]
				// The extra parameter's type lives in an assembly that is deployed nowhere, so this
				// type cannot load and the factory it declares is unreachable.
				public static Morris.Roslynk.Plugins.V1.IRoslynkPlugin? Create(Morris.Roslynk.Plugins.V1.IPluginContext context, MissingLib.Missing missing) => null;
			}
			""",
			contractReference: true,
			extraReferencePaths: [missingLibPath]);
		File.Delete(missingLibPath);
		PluginLoader loader = new();

		PluginLoadResult loadResult = loader.Load(Candidate(pluginDirectory));
		PluginFactoryResult factoryResult = loader.FindFactory(loadResult);

		Assert.False(factoryResult.IsSuccess);
		Assert.Equal(PluginDiscoveryStage.DependencyUnresolved, factoryResult.Stage);
	}

	[Fact]
	public void WhenATypeOtherThanTheFactoryCannotLoad_ThenTheFactoryIsStillFound()
	{
		string pluginDirectory = Path.Combine(Directory, "CallGraph");
		_ = System.IO.Directory.CreateDirectory(pluginDirectory);
		string missingLibPath = EmitMissingLib(pluginDirectory);
		TestPluginCompiler.Emit(
			Path.Combine(pluginDirectory, "CallGraph.Roslynk.Plugin.dll"),
			TestPluginCompiler.ApiVersionV1Source + """

			public static class Factories
			{
				[Morris.Roslynk.Plugins.V1.RoslynkPluginFactory]
				public static Morris.Roslynk.Plugins.V1.IRoslynkPlugin? Create(Morris.Roslynk.Plugins.V1.IPluginContext context) => null;
			}

			internal static class Broken
			{
				internal static readonly MissingLib.Missing? Missing;
			}
			""",
			contractReference: true,
			extraReferencePaths: [missingLibPath]);
		File.Delete(missingLibPath);
		PluginLoader loader = new();

		PluginLoadResult loadResult = loader.Load(Candidate(pluginDirectory));
		PluginFactoryResult factoryResult = loader.FindFactory(loadResult);

		Assert.True(factoryResult.IsSuccess, factoryResult.Reason);
	}

	[Fact]
	public void WhenTheEntryIsNotAnAssembly_ThenTheLoadReportsItAsMalformed()
	{
		string pluginDirectory = Path.Combine(Directory, "Broken");
		_ = System.IO.Directory.CreateDirectory(pluginDirectory);
		File.WriteAllText(Path.Combine(pluginDirectory, "Broken.Roslynk.Plugin.dll"), "this is not a portable executable");

		PluginLoadResult result = new PluginLoader().Load(Candidate(pluginDirectory));

		Assert.False(result.IsSuccess);
		Assert.Equal(PluginDiscoveryStage.Malformed, result.Stage);
	}

	private PluginCandidate Candidate(string pluginDirectory)
	{
		string folderName = System.IO.Path.GetFileName(pluginDirectory);
		return new PluginCandidate(folderName, pluginDirectory, Path.Combine(pluginDirectory, $"{folderName}.Roslynk.Plugin.dll"), ApiVersion.V1);
	}

	private string EmitMissingLib(string pluginDirectory)
	{
		string path = Path.Combine(pluginDirectory, "MissingLib.dll");
		TestPluginCompiler.Emit(
			path,
			"""
			namespace MissingLib
			{
				public class Missing { }
			}
			""",
			contractReference: false,
			assemblyName: "MissingLib");
		return path;
	}

	private string StagePlugin(string source, bool contractReference)
	{
		string pluginDirectory = Path.Combine(Directory, "CallGraph");
		TestPluginCompiler.Emit(Path.Combine(pluginDirectory, "CallGraph.Roslynk.Plugin.dll"), source, contractReference: contractReference);
		return pluginDirectory;
	}
}
