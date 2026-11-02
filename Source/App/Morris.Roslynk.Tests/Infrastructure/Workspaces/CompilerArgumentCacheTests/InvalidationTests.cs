using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Tests.Infrastructure.Workspaces.CompilerArgumentCacheTests;

/// <summary>
/// A change to anything the captured arguments were worked out from loads through MSBuild, which captures again;
/// an ordinary source edit, a file-local <c>#define</c> included, keeps the cache.
/// </summary>
public class InvalidationTests : MutatingTestBase
{
	[Fact]
	public async Task WhenASourceFileIsEdited_ThenTheNextLoadUsesTheCacheAndReadsTheEdit()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		string greeter = Path.Combine(LibDirectory(solutionPath), "Greeter.cs");
		File.WriteAllText(greeter, File.ReadAllText(greeter).Replace("Hello", "Howdy"));

		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(solutionPath, cache);

		Assert.True(subject.LoadedFromCache, cache.LastLoadReason);
		Document document = subject.Solution.Projects.First(p => p.Name.StartsWith("Lib", StringComparison.Ordinal)).Documents.Single(d => d.Name == "Greeter.cs");
		Assert.Contains("Howdy", (await document.GetTextAsync()).ToString());
	}

	[Fact]
	public async Task WhenAFileLocalDefineIsAdded_ThenTheNextLoadUsesTheCache()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		string greeter = Path.Combine(LibDirectory(solutionPath), "Greeter.cs");
		File.WriteAllText(greeter, "#define LOCAL_ONLY\n" + File.ReadAllText(greeter));

		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(solutionPath, cache);

		Assert.True(subject.LoadedFromCache, cache.LastLoadReason);
	}

	[Fact]
	public async Task WhenTheProjectFileChangesItsDefineConstants_ThenTheNextLoadGoesThroughMSBuildAndSeesThem()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		string project = Path.Combine(LibDirectory(solutionPath), "Lib.csproj");
		File.WriteAllText(project, File.ReadAllText(project).Replace("</PropertyGroup>", "  <DefineConstants>$(DefineConstants);FROM_PROJECT</DefineConstants>\n  </PropertyGroup>"));

		using SolutionWorkspace reloaded = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "Lib.csproj changed", define: "FROM_PROJECT");
	}

	[Fact]
	public async Task WhenADirectoryBuildPropsIsAddedAboveTheProjects_ThenTheNextLoadGoesThroughMSBuildAndSeesIt()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		File.WriteAllText(
			Path.Combine(Path.GetDirectoryName(solutionPath)!, "Directory.Build.props"),
			"<Project><PropertyGroup><DefineConstants>$(DefineConstants);FROM_PROPS</DefineConstants></PropertyGroup></Project>");

		using SolutionWorkspace reloaded = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "Directory.Build.props was added", define: "FROM_PROPS");
	}

	[Fact]
	public async Task WhenAFileAProjectImportsChanges_ThenTheNextLoadGoesThroughMSBuildAndSeesIt()
	{
		string solutionPath = Scratch(() => TestSolutions.CreateScratchLinkedSolution());
		string shared = Path.Combine(Path.GetDirectoryName(solutionPath)!, "build", "Shared.props");
		Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
		File.WriteAllText(shared, "<Project />");
		string project = Path.Combine(LibDirectory(solutionPath), "Lib.csproj");
		File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "  <Import Project=\"$(MSBuildThisFileDirectory)../build/Shared.props\" />\n</Project>"));
		CompilerArgumentCache cache = TestCaches.New();
		await LoadAsync(solutionPath, cache, fromCache: false);

		File.WriteAllText(shared, "<Project><PropertyGroup><DefineConstants>$(DefineConstants);FROM_IMPORT</DefineConstants></PropertyGroup></Project>");

		using SolutionWorkspace reloaded = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "Shared.props changed", define: "FROM_IMPORT");
	}

	[Fact]
	public async Task WhenAProjectIsRestoredAgain_ThenTheNextLoadGoesThroughMSBuild()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		string assets = Path.Combine(LibDirectory(solutionPath), "obj", "project.assets.json");
		File.SetLastWriteTimeUtc(assets, DateTime.UtcNow.AddMinutes(1));

		using SolutionWorkspace reloaded = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "project.assets.json changed");
	}

	[Fact]
	public async Task WhenAnEnvironmentVariableAProjectReadsChanges_ThenTheNextLoadGoesThroughMSBuildAndSeesIt()
	{
		string variable = $"ROSLYNK_TEST_{Guid.NewGuid():N}";
		string solutionPath = Scratch(() => TestSolutions.CreateScratchLinkedSolution());
		string project = Path.Combine(LibDirectory(solutionPath), "Lib.csproj");
		File.WriteAllText(project, File.ReadAllText(project).Replace(
			"</Project>",
			$"  <PropertyGroup Condition=\"'$({variable})' == 'on'\"><DefineConstants>$(DefineConstants);FROM_ENVIRONMENT</DefineConstants></PropertyGroup>\n</Project>"));
		CompilerArgumentCache cache = TestCaches.New();
		await LoadAsync(solutionPath, cache, fromCache: false);

		Environment.SetEnvironmentVariable(variable, "on");
		try
		{
			using SolutionWorkspace reloaded = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, $"the environment variable {variable} changed", define: "FROM_ENVIRONMENT");
		}
		finally
		{
			Environment.SetEnvironmentVariable(variable, null);
		}
	}

	[Fact]
	public async Task WhenASourceFileIsAdded_ThenTheNextLoadGoesThroughMSBuildAndListsIt()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		File.WriteAllText(Path.Combine(LibDirectory(solutionPath), "Added.cs"), "namespace Linked.Library;\n\npublic static class Added;\n");

		using SolutionWorkspace subject = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "a source file was added to or deleted from Lib.csproj's folder");

		Assert.Contains(subject.Solution.Projects.SelectMany(p => p.Documents), d => d.Name == "Added.cs");
	}

	[Fact]
	public async Task WhenASourceFileIsDeleted_ThenTheNextLoadGoesThroughMSBuild()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		File.Delete(Path.Combine(LibDirectory(solutionPath), "Greeter.cs"));

		using SolutionWorkspace reloaded = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "Greeter.cs was deleted");
	}

	[Fact]
	public async Task WhenTheSolutionFileChanges_ThenTheNextLoadGoesThroughMSBuild()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		File.WriteAllText(solutionPath, File.ReadAllText(solutionPath).Replace("<Project Path=\"Lib/Lib.csproj\" />\n", ""));

		using SolutionWorkspace subject = await AssertReloadedThroughMSBuildAsync(solutionPath, cache, "LinkedSolution.slnx changed");
	}

	[Fact]
	public async Task WhenTheCachedLoadIsInvalidated_ThenTheLoadAfterTheReloadUsesTheCacheAgain()
	{
		(string solutionPath, CompilerArgumentCache cache) = await LoadedOnceAsync();
		File.WriteAllText(Path.Combine(LibDirectory(solutionPath), "Added.cs"), "namespace Linked.Library;\n\npublic static class Added;\n");
		await LoadAsync(solutionPath, cache, fromCache: false);

		using SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(solutionPath, cache);

		Assert.True(subject.LoadedFromCache, cache.LastLoadReason);
		Assert.Contains(subject.Solution.Projects.SelectMany(p => p.Documents), d => d.Name == "Added.cs");
	}

	/// <summary>A fresh linked solution (Lib, multi-targeted, and App), loaded through MSBuild into a new cache.</summary>
	private async Task<(string SolutionPath, CompilerArgumentCache Cache)> LoadedOnceAsync()
	{
		string solutionPath = Scratch(() => TestSolutions.CreateScratchLinkedSolution());
		CompilerArgumentCache cache = TestCaches.New();
		await LoadAsync(solutionPath, cache, fromCache: false);
		return (solutionPath, cache);
	}

	private static async Task LoadAsync(string solutionPath, CompilerArgumentCache cache, bool fromCache)
	{
		using SolutionWorkspace loaded = await SolutionWorkspace.LoadAsync(solutionPath, cache);
		Assert.Equal(fromCache, loaded.LoadedFromCache);
		Assert.True(cache.LastStoreReason == "", $"not stored: {cache.LastStoreReason}");
	}

	/// <summary>
	/// Loads once more, expecting MSBuild for <paramref name="reason"/> (ending the reason the cache gave) and, when
	/// given, every Lib framework to define <paramref name="define"/>.
	/// </summary>
	private static async Task<SolutionWorkspace> AssertReloadedThroughMSBuildAsync(string solutionPath, CompilerArgumentCache cache, string reason, string? define = null)
	{
		SolutionWorkspace subject = await SolutionWorkspace.LoadAsync(solutionPath, cache);

		Assert.False(subject.LoadedFromCache);
		Assert.EndsWith(reason, cache.LastLoadReason, StringComparison.Ordinal);
		if (define is not null)
		{
			foreach (Project lib in subject.Solution.Projects.Where(p => p.Name.StartsWith("Lib", StringComparison.Ordinal)))
				Assert.Contains(define, ((CSharpParseOptions)lib.ParseOptions!).PreprocessorSymbolNames);
		}

		return subject;
	}

	private static string LibDirectory(string solutionPath) => Path.Combine(Path.GetDirectoryName(solutionPath)!, "Lib");
}
