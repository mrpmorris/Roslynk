using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins.PluginCatalogTests;

public class LoadTests
{
	private const string SamplePlugin = """
		using System;
		using System.Threading;
		using System.Threading.Tasks;
		using ModelContextProtocol.Server;
		using Morris.Roslynk.Plugins;

		namespace Sample;

		public sealed class SamplePlugin : IRoslynkPlugin
		{
			public Type SolutionType => typeof(Microsoft.CodeAnalysis.Solution);
			public Type ContractType => typeof(IRoslynkPlugin);

			public Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping) => Task.CompletedTask;
		}

		[McpServerToolType]
		public sealed class SampleTools
		{
		}
		""";

	[Fact]
	public void WhenAPluginFolderHasAPlugin_ThenItIsCreatedAndItsToolTypesFound()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Sample", SamplePlugin);

		PluginCatalog subject = PluginCatalog.Load(plugins);

		Assert.Empty(subject.Errors);
		LoadedPlugin plugin = Assert.Single(subject.Plugins);
		Assert.Equal("Sample", plugin.Name);
		Assert.IsAssignableFrom<IRoslynkPlugin>(Assert.Single(plugin.Instances));
		Assert.Equal("SampleTools", Assert.Single(plugin.ToolTypes).Name);
	}

	[Fact]
	public void WhenAPluginShipsItsOwnCopiesOfTheSharedAssemblies_ThenTheDaemonsAreUsed()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Sample", SamplePlugin);
		// What `dotnet build` puts beside a plugin that references the contract package: its own copies.
		string folder = Path.Combine(plugins, "Sample");
		foreach (string shared in new[] { typeof(Solution), typeof(IRoslynkPlugin), typeof(McpServerToolTypeAttribute) }.Select(type => type.Assembly.Location))
			File.Copy(shared, Path.Combine(folder, Path.GetFileName(shared)));

		IRoslynkPlugin instance = Assert.Single(Assert.Single(PluginCatalog.Load(plugins).Plugins).Instances);

		Assert.Same(typeof(Solution), instance.GetType().GetProperty("SolutionType")!.GetValue(instance));
		Assert.Same(typeof(IRoslynkPlugin), instance.GetType().GetProperty("ContractType")!.GetValue(instance));
	}

	[Fact]
	public void WhenAPluginHasADependencyTheDaemonLacks_ThenItIsLoadedFromThePluginsFolder()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		string folder = Path.Combine(plugins, "WithHelper");
		string helper = TestPlugins.WriteAssembly(folder, "WithHelper.Helper", """
			namespace WithHelper;
			public static class Helper { public static string Name => "helper"; }
			""");
		TestPlugins.WriteAssembly(folder, "WithHelper", """
			using System.Threading;
			using System.Threading.Tasks;
			using Morris.Roslynk.Plugins;

			namespace WithHelper;

			public sealed class WithHelperPlugin : IRoslynkPlugin
			{
				public string HelperName => Helper.Name;

				public Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping) => Task.CompletedTask;
			}
			""", helper);

		IRoslynkPlugin instance = Assert.Single(Assert.Single(PluginCatalog.Load(plugins).Plugins).Instances);

		Assert.Equal("helper", instance.GetType().GetProperty("HelperName")!.GetValue(instance));
	}

	[Fact]
	public void WhenAPluginIsBuiltAgainstANewerSharedAssemblyThanTheDaemons_ThenItIsReportedAtLoad()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		string folder = Path.Combine(plugins, "TooNew");
		// A stand-in for a newer MCP SDK than the daemon ships: same name, higher version.
		string newer = TestPlugins.WriteAssembly(Path.Combine(folder, "build"), "ModelContextProtocol.Core", """
			[assembly: System.Reflection.AssemblyVersion("99.0.0.0")]
			namespace ModelContextProtocol.Server;
			[System.AttributeUsage(System.AttributeTargets.Class)]
			public sealed class McpServerToolTypeAttribute : System.Attribute { }
			""");
		TestPlugins.WriteAssembly(folder, "TooNew", """
			using ModelContextProtocol.Server;

			namespace TooNew;

			[McpServerToolType]
			public sealed class TooNewTools { }
			""", newer);

		PluginCatalog subject = PluginCatalog.Load(plugins);

		Assert.Empty(subject.Plugins);
		string error = Assert.Single(subject.Errors);
		Assert.StartsWith("TooNew: It was built against ModelContextProtocol.Core 99.0.0.0; this daemon has ", error);
	}

	[Fact]
	public void WhenAPluginsConstructorThrows_ThenItsOwnMessageIsReported()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Throws", """
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Morris.Roslynk.Plugins;

			namespace Throws;

			public sealed class ThrowsPlugin : IRoslynkPlugin
			{
				public ThrowsPlugin() => throw new InvalidOperationException("Set THROWS_SOLUTION first.");

				public Task RunAsync(ISolutionProvider solutionProvider, CancellationToken stopping) => Task.CompletedTask;
			}
			""");

		PluginCatalog subject = PluginCatalog.Load(plugins);

		Assert.Equal("Throws: Set THROWS_SOLUTION first.", Assert.Single(subject.Errors));
	}

	[Fact]
	public void WhenAFolderHasNoAssemblyOfItsName_ThenItIsReportedAndTheOthersStillLoad()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Sample", SamplePlugin);
		Directory.CreateDirectory(Path.Combine(plugins, "Empty"));

		PluginCatalog subject = PluginCatalog.Load(plugins);

		Assert.Equal("Sample", Assert.Single(subject.Plugins).Name);
		Assert.StartsWith("Empty: ", Assert.Single(subject.Errors));
	}

	[Fact]
	public void WhenThePluginsFolderDoesNotExist_ThenTheCatalogIsEmpty()
	{
		PluginCatalog subject = PluginCatalog.Load(Path.Combine(TestPlugins.NewPluginsFolder(), "missing"));

		Assert.Empty(subject.Plugins);
		Assert.Empty(subject.Errors);
	}
}
