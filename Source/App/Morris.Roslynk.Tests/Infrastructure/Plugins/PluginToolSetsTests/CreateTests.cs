using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins.PluginToolSetsTests;

public class CreateTests
{
	[Fact]
	public void WhenOnePluginsToolsCannotBeCreated_ThenItIsReportedAndTheOthersAreServed()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Duplicate", """
			using ModelContextProtocol.Server;

			namespace Duplicate;

			[McpServerToolType]
			public sealed class DuplicateTools
			{
				[McpServerTool(Name = "lookup")] public static string ByNumber(int number) => "";
				[McpServerTool(Name = "lookup")] public static string ByName(string name) => "";
			}
			""");
		TestPlugins.Write(plugins, "Good", """
			using ModelContextProtocol.Server;

			namespace Good;

			[McpServerToolType]
			public sealed class GoodTools
			{
				[McpServerTool(Name = "good")] public static string Good() => "good";
			}
			""");
		using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

		var subject = new PluginToolSets(PluginCatalog.Load(plugins), services);

		Assert.Null(subject.For("Duplicate"));
		Assert.Contains("lookup", subject.ErrorFor("Duplicate"));
		Assert.StartsWith("Duplicate: ", Assert.Single(subject.Errors));
		Assert.Equal("good", Assert.Single(subject.For("Good")!).ProtocolTool.Name);
	}

	[Fact]
	public void WhenAToolMethodIsInheritedFromABaseClass_ThenItIsServed()
	{
		string plugins = TestPlugins.NewPluginsFolder();
		TestPlugins.Write(plugins, "Inherits", """
			using ModelContextProtocol.Server;

			namespace Inherits;

			public abstract class BaseTools
			{
				[McpServerTool(Name = "inherited")] public string Inherited() => "inherited";
			}

			[McpServerToolType]
			public sealed class DerivedTools : BaseTools
			{
			}
			""");
		using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

		var subject = new PluginToolSets(PluginCatalog.Load(plugins), services);

		Assert.Equal("inherited", Assert.Single(subject.For("Inherits")!).ProtocolTool.Name);
	}
}
