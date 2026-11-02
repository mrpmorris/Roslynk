using Microsoft.CodeAnalysis;
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
	public void WhenAPluginIsLoaded_ThenItUsesTheDaemonsRoslynNotItsOwn()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Sample", SamplePlugin);

		IRoslynkPlugin instance = Assert.Single(Assert.Single(PluginCatalog.Load(plugins).Plugins).Instances);
		object? solutionType = instance.GetType().GetProperty("SolutionType")!.GetValue(instance);

		Assert.Same(typeof(Solution), solutionType);
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
