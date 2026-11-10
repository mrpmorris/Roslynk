using Morris.Roslynk.Infrastructure.Plugins;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

public class PluginProberTests : IDisposable
{
	private readonly string Directory;

	public PluginProberTests()
	{
		Directory = Path.Combine(Path.GetTempPath(), "roslynk-prober-tests-" + Guid.NewGuid().ToString("N"));
		_ = System.IO.Directory.CreateDirectory(Directory);
	}

	public void Dispose()
	{
		try
		{
			System.IO.Directory.Delete(Directory, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void WhenTheEntryAssemblyCarriesTheAttribute_ThenTheApiVersionIsRead()
	{
		string path = Path.Combine(Directory, "Probe.Roslynk.Plugin.dll");
		TestPluginCompiler.Emit(path);

		PluginProbeResult result = new PluginProber().Probe(path);

		Assert.True(result.IsSuccess, result.Reason);
		Assert.Equal(ApiVersion.V1, result.ApiVersion);
	}

	[Fact]
	public void WhenTheEntryAssemblyHasNoAttribute_ThenTheProbeReportsIt()
	{
		// The contract assembly itself is a real PE that carries no RoslynkApiVersion assembly attribute.
		string path = typeof(ApiVersion).Assembly.Location;

		PluginProbeResult result = new PluginProber().Probe(path);

		Assert.False(result.IsSuccess);
		Assert.Equal(PluginDiscoveryStage.MissingApiVersionAttribute, result.Stage);
	}

	[Fact]
	public void WhenTheFileIsNotAnAssembly_ThenTheProbeReportsMalformed()
	{
		string path = Path.Combine(Directory, "Broken.Roslynk.Plugin.dll");
		File.WriteAllText(path, "this is not a portable executable");

		PluginProbeResult result = new PluginProber().Probe(path);

		Assert.False(result.IsSuccess);
		Assert.Equal(PluginDiscoveryStage.Malformed, result.Stage);
	}

	[Fact]
	public void WhenTheAttributeTypeCannotBeResolved_ThenTheBlobDecodeReadsTheVersion()
	{
		// Self-contained: the attribute type is declared in the same source, so no contract assembly
		// is deployed alongside — exactly the case the typed read cannot resolve.
		string path = Path.Combine(Directory, "SelfContained.Roslynk.Plugin.dll");
		TestPluginCompiler.Emit(
			path,
			"""
			[assembly: Morris.Roslynk.Plugins.V1.RoslynkApiVersion(Morris.Roslynk.Plugins.V1.ApiVersion.V1)]

			namespace Morris.Roslynk.Plugins.V1
			{
				[System.AttributeUsage(System.AttributeTargets.Assembly)]
				public sealed class RoslynkApiVersionAttribute : System.Attribute
				{
					public RoslynkApiVersionAttribute(ApiVersion apiVersion) { }
				}

				public enum ApiVersion
				{
					V1 = 1,
				}
			}
			""",
			contractReference: false);

		Assert.Equal(ApiVersion.V1, PluginProber.ReadApiVersionFromBlob(path));
	}

	[Fact]
	public void WhenTheAssemblyHasNoAttribute_ThenTheBlobDecodeReturnsNull()
	{
		string path = typeof(ApiVersion).Assembly.Location;

		Assert.Null(PluginProber.ReadApiVersionFromBlob(path));
	}

	[Fact]
	public void WhenTheProbeSucceeds_ThenReferencedAssembliesAreListed()
	{
		string path = Path.Combine(Directory, "Probe.Roslynk.Plugin.dll");
		TestPluginCompiler.Emit(path);

		PluginProbeResult result = new PluginProber().Probe(path);

		Assert.True(result.IsSuccess, result.Reason);
		Assert.Contains(result.ReferencedAssemblies, reference => reference.Name == "Morris.Roslynk.Plugins.V1");
	}
}
