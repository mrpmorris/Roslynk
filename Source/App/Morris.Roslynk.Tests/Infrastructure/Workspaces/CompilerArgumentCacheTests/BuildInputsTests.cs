using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerArgumentCacheTests;

/// <summary>Which environment variables a load is recorded as depending on, beyond those its own files name.</summary>
public class BuildInputsTests : MutatingTestBase
{
	[Fact]
	public void WhenOnlyTheSdksBuildFilesNameAVariable_ThenItIsRecorded()
	{
		string root = NewFolder();
		string sdk = Path.Combine(root, "dotnet", "sdk", "1.0.0");
		Write(Path.Combine(sdk, "Sdks", "Example", "Sdk.targets"), "<Project><PropertyGroup><X>$(ROSLYNK_SDK_ONLY)</X></PropertyGroup></Project>");
		string project = Write(Path.Combine(root, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

		BuildInputs inputs = BuildInputs.Of(Path.Combine(root, "src", "App.slnx"), [new CapturedArguments(project, "", null, [])], [sdk]);

		Assert.Null(inputs.Uncertain);
		Assert.Contains("ROSLYNK_SDK_ONLY", inputs.EnvironmentVariables);
	}

	[Fact]
	public void WhenOnlyAnImportedPackagesBuildFileNamesAVariable_ThenItIsRecorded()
	{
		string root = NewFolder();
		string packages = Path.Combine(root, "packages") + Path.DirectorySeparatorChar;
		Write(Path.Combine(packages, "example", "1.0.0", "build", "Example.props"),
			"<Project><Import Project=\"$(MSBuildThisFileDirectory)Inner.props\" /></Project>");
		Write(Path.Combine(packages, "example", "1.0.0", "build", "Inner.props"),
			"<Project><PropertyGroup><X>$([System.Environment]::GetEnvironmentVariable('ROSLYNK_PACKAGE_ONLY'))</X></PropertyGroup></Project>");
		string project = Write(Path.Combine(root, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
		string assets = Write(Path.Combine(root, "src", "App", "obj", "project.assets.json"), "{}");
		Write(Path.Combine(root, "src", "App", "obj", "App.csproj.nuget.g.props"), $"""
			<Project>
			  <PropertyGroup><NuGetPackageRoot>{packages}</NuGetPackageRoot></PropertyGroup>
			  <ImportGroup><Import Project="$(NuGetPackageRoot)example/1.0.0/build/Example.props" /></ImportGroup>
			</Project>
			""");

		BuildInputs inputs = BuildInputs.Of(Path.Combine(root, "src", "App.slnx"), [new CapturedArguments(project, "", assets, [])], []);

		Assert.Null(inputs.Uncertain);
		Assert.Contains("ROSLYNK_PACKAGE_ONLY", inputs.EnvironmentVariables);
		Assert.Contains(Path.Combine(root, "src", "App", "obj", "App.csproj.nuget.g.props"), inputs.Files);
	}

	private string NewFolder()
	{
		string path = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"));
		TrackScratch(path);
		return path;
	}

	private static string Write(string path, string text)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		return path;
	}
}
