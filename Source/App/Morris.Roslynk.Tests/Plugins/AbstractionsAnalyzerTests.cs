using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Plugins.Abstractions;
using Morris.Roslynk.Plugins.Abstractions.Analyzer;

namespace Morris.Roslynk.Tests.Plugins;

/// <summary>
/// The four convention rules (RKPL001-RKPL004), applied only to projects whose output assembly name
/// ends in .Roslynk.Plugin. Factory shape is deliberately absent: the static abstract member on
/// IPluginFactory makes a wrong factory a compile error, not an analyzer finding.
/// </summary>
public class AbstractionsAnalyzerTests
{
	private static readonly DiagnosticAnalyzer Analyzer = new PluginConventionAnalyzer();

	[Fact]
	public async Task WhenTheEntryAssemblyCarriesTheFactoryClaim_ThenNoDiagnosticIsRaised()
	{
		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync("Sample.Roslynk.Plugin", WellFormedPlugin);

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id.StartsWith("RKPL", StringComparison.Ordinal));
	}

	[Fact]
	public async Task WhenTheFactoryClaimIsMissing_ThenRkpl001IsReported()
	{
		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync("Sample.Roslynk.Plugin", WellFormedPluginWithoutAttribute);

		Diagnostic diagnostic = Assert.Single(diagnostics, candidate => candidate.Id == "RKPL001");
		Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
		Assert.Contains("Sample.Roslynk.Plugin", diagnostic.GetMessage());
	}

	[Fact]
	public async Task WhenTwoFactoryClaimsAreApplied_ThenRkpl002IsReported()
	{
		string source = WellFormedPlugin.Replace(
			"[assembly: PluginFactory<PluginFactory>]",
			"[assembly: PluginFactory<PluginFactory>]\n[assembly: PluginFactory<SecondFactory>]\n\npublic sealed class SecondFactory : IPluginFactory\n{\n\tpublic static IPlugin? Create(IPluginContext context) => new Plugin();\n}");

		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync("Sample.Roslynk.Plugin", source);

		Diagnostic diagnostic = Assert.Single(diagnostics, candidate => candidate.Id == "RKPL002");
		Assert.Contains("2 PluginFactory<> applications", diagnostic.GetMessage());
	}

	[Theory]
	[InlineData("a.Roslynk.Plugin")]
	[InlineData("3Tool.Roslynk.Plugin")]
	[InlineData("ThisNameIsFarTooLongForAPluginNameToBeValid.Roslynk.Plugin")]
	public async Task WhenTheOutputAssemblyNameDoesNotMatchThePluginNameRules_ThenRkpl003IsReported(string assemblyName)
	{
		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync(assemblyName, WellFormedPlugin);

		Diagnostic diagnostic = Assert.Single(diagnostics, candidate => candidate.Id == "RKPL003");
		Assert.Contains(assemblyName, diagnostic.GetMessage());
	}

	[Fact]
	public async Task WhenAPluginToolLacksTheSolutionIdFirstParameter_ThenRkpl004IsReported()
	{
		string source = WellFormedPlugin.Replace(
			"public string CountProjects(string solutionId, CancellationToken cancellationToken) => \"\";",
			"public string CountProjects(string path, CancellationToken cancellationToken) => \"\";");

		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync("Sample.Roslynk.Plugin", source);

		Diagnostic diagnostic = Assert.Single(diagnostics, candidate => candidate.Id == "RKPL004");
		Assert.Contains("CountProjects", diagnostic.GetMessage());
	}

	[Fact]
	public async Task WhenAPluginToolLacksACancellationTokenParameter_ThenRkpl004IsReported()
	{
		string source = WellFormedPlugin.Replace(
			"public string CountProjects(string solutionId, CancellationToken cancellationToken) => \"\";",
			"public string CountProjects(string solutionId) => \"\";");

		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync("Sample.Roslynk.Plugin", source);

		Assert.Single(diagnostics, candidate => candidate.Id == "RKPL004");
	}

	[Fact]
	public async Task WhenTheAssemblyIsNotAPluginEntry_ThenTheAnalyzerIsSilent()
	{
		// The suffix is the gate: an ordinary project with the same missing attribute raises nothing.
		IImmutableList<Diagnostic> diagnostics = await AnalyzeAsync("SomethingElse", WellFormedPluginWithoutAttribute);

		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id.StartsWith("RKPL", StringComparison.Ordinal));
	}

	private const string WellFormedPlugin = """
		using System.Threading;
		using Morris.Roslynk.Plugins.Abstractions;
		using ModelContextProtocol.Server;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(System.Exception error) { }
			public void OnNext(ISolutionEvent value) { }

			[McpServerTool]
			public string CountProjects(string solutionId, CancellationToken cancellationToken) => "";
		}
		""";

	private const string WellFormedPluginWithoutAttribute = """
		using System.Threading;
		using Morris.Roslynk.Plugins.Abstractions;
		using ModelContextProtocol.Server;

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(System.Exception error) { }
			public void OnNext(ISolutionEvent value) { }

			[McpServerTool]
			public string CountProjects(string solutionId, CancellationToken cancellationToken) => "";
		}
		""";

	private static async Task<IImmutableList<Diagnostic>> AnalyzeAsync(string assemblyName, string source)
	{
		string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
		var compilation = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source)],
		[
			MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Private.CoreLib.dll")),
			MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
			MetadataReference.CreateFromFile(typeof(IPluginContext).Assembly.Location),
			MetadataReference.CreateFromFile(typeof(ModelContextProtocol.Server.McpServerToolAttribute).Assembly.Location),
		],
		new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		var compilationWithAnalyzers = compilation.WithAnalyzers([Analyzer]);
		return await compilationWithAnalyzers.GetAllDiagnosticsAsync();
	}
}
