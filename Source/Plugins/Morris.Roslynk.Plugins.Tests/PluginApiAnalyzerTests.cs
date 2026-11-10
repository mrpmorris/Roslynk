using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Plugins.V1;
using Morris.Roslynk.Plugins.V1.Analyzer;

namespace Morris.Roslynk.Plugins.Tests;

/// <summary>
/// Drives the PluginApiAnalyzer over in-memory compilations. The plugin snippets reference the real
/// contract assembly; the [McpServerTool] attribute is matched by fully-qualified name, so a stub in
/// the snippet stands in for the MCP SDK.
/// </summary>
public class PluginApiAnalyzerTests
{
	private const string ValidFactoryBlock = """
public static class PluginFactory
{
	[RoslynkPluginFactory]
	public static IRoslynkPlugin? Create(IPluginContext context) => new CallGraphPlugin();
}
""";

	[Fact]
	public async Task WhenThePluginIsValid_ThenNoDiagnosticsAreReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(ValidFactoryBlock));

		Assert.Empty(diagnostics);
	}

	[Fact]
	public async Task WhenTheEntryAssemblyHasNoFactory_ThenMissingFactoryIsReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(string.Empty));

		AssertSingleDiagnostic(diagnostics, "RK0001");
	}

	[Fact]
	public async Task WhenTwoFactoriesAreDeclared_ThenMultipleFactoriesIsReported()
	{
		string secondFactory = """
public static class AnotherFactory
{
	[RoslynkPluginFactory]
	public static IRoslynkPlugin? Create(IPluginContext context) => new CallGraphPlugin();
}
""";

		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(ValidFactoryBlock, secondFactory));

		AssertSingleDiagnostic(diagnostics, "RK0002");
	}

	[Fact]
	public async Task WhenTheFactoryIsNotPubliclyAccessible_ThenFactoryNotPubliclyAccessibleIsReported()
	{
		string factoryBlock = """
internal static class PluginFactory
{
	[RoslynkPluginFactory]
	public static IRoslynkPlugin? Create(IPluginContext context) => new CallGraphPlugin();
}
""";

		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(factoryBlock));

		AssertSingleDiagnostic(diagnostics, "RK0003");
	}

	[Fact]
	public async Task WhenTheFactoryIsNotStatic_ThenFactoryNotPublicOrStaticIsReported()
	{
		string factoryBlock = """
public static class PluginFactory
{
	[RoslynkPluginFactory]
	public IRoslynkPlugin? Create(IPluginContext context) => new CallGraphPlugin();
}
""";

		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(factoryBlock));

		AssertSingleDiagnostic(diagnostics, "RK0004");
	}

	[Fact]
	public async Task WhenTheFactoryMethodIsGeneric_ThenFactoryOrDeclaringTypeGenericIsReported()
	{
		string factoryBlock = """
public static class PluginFactory
{
	[RoslynkPluginFactory]
	public static IRoslynkPlugin? Create<T>(IPluginContext context) => new CallGraphPlugin();
}
""";

		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(factoryBlock));

		AssertSingleDiagnostic(diagnostics, "RK0005");
	}

	[Fact]
	public async Task WhenTheFactoryTakesNoParameter_ThenFactoryIncorrectParametersIsReported()
	{
		string factoryBlock = """
public static class PluginFactory
{
	[RoslynkPluginFactory]
	public static IRoslynkPlugin? Create() => new CallGraphPlugin();
}
""";

		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(factoryBlock));

		AssertSingleDiagnostic(diagnostics, "RK0006");
	}

	[Fact]
	public async Task WhenTheFactoryReturnsTheConcretePlugin_ThenFactoryIncorrectReturnTypeIsReported()
	{
		string factoryBlock = """
public static class PluginFactory
{
	[RoslynkPluginFactory]
	public static CallGraphPlugin Create(IPluginContext context) => new CallGraphPlugin();
}
""";

		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(factoryBlock));

		AssertSingleDiagnostic(diagnostics, "RK0007");
	}

	[Fact]
	public async Task WhenTheAssemblyNameBreaksThePluginNameRules_ThenPluginAssemblyNameMismatchIsReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(ValidFactoryBlock), "Ab.Roslynk.Plugin");

		AssertSingleDiagnostic(diagnostics, "RK0008");
	}

	[Fact]
	public async Task WhenTheAssemblyIsNotAPluginEntry_ThenNothingIsReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(PluginSource(ValidFactoryBlock), "SomeRandomLibrary");

		Assert.Empty(diagnostics);
	}

	[Fact]
	public async Task WhenTheApiVersionAttributeIsMissing_ThenMissingApiVersionAttributeIsReported()
	{
		string source = PluginSource(ValidFactoryBlock)
			.Replace("[assembly: RoslynkApiVersion(ApiVersion.V1)]", string.Empty)
			.Replace("[assembly:RoslynkApiVersion(ApiVersion.V1)]", string.Empty);

		ImmutableArray<Diagnostic> diagnostics = await Analyze(source);

		AssertSingleDiagnostic(diagnostics, "RK0013");
	}

	[Fact]
	public async Task WhenAToolMethodIsMissingSolutionId_ThenPluginToolFirstParameterNotSolutionIdIsReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(
			PluginSource(ValidFactoryBlock),
			additionalTree: ToolTree("public static void Tool(string path) { }"));

		AssertSingleDiagnostic(diagnostics, "RK0010");
	}

	[Fact]
	public async Task WhenAToolMethodIsMissingCancellationToken_ThenPluginToolMissingCancellationTokenIsReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(
			PluginSource(ValidFactoryBlock),
			additionalTree: ToolTree("public static void Tool(string solutionId) { }"));

		AssertSingleDiagnostic(diagnostics, "RK0011");
	}

	[Fact]
	public async Task WhenAToolNameContainsDoubleUnderscore_ThenPluginToolNameContainsDoubleUnderscoreIsReported()
	{
		ImmutableArray<Diagnostic> diagnostics = await Analyze(
			PluginSource(ValidFactoryBlock),
			additionalTree: ToolTree("public static void a__b(string solutionId, CancellationToken cancellationToken) { }"));

		AssertSingleDiagnostic(diagnostics, "RK0012");
	}

	[Fact]
	public async Task WhenTheExposedToolNameExceeds64Characters_ThenPluginToolNameInvalidIsReported()
	{
		string longName = "Tool" + new string('x', 60);

		ImmutableArray<Diagnostic> diagnostics = await Analyze(
			PluginSource(ValidFactoryBlock),
			additionalTree: ToolTree($"public static void {longName}(string solutionId, CancellationToken cancellationToken) {{ }}"));

		AssertSingleDiagnostic(diagnostics, "RK0009");
	}

	private static string PluginSource(string factoryBlock, string additionalTypes = "")
	{
		const string Head = """
using Morris.Roslynk.Plugins.V1;

[assembly: RoslynkApiVersion(ApiVersion.V1)]

namespace CallGraph
{
	public class CallGraphPlugin : IRoslynkPlugin
	{
		public void OnCompleted() { }
		public void OnError(Exception error) { }
		public void OnNext(ISolutionEvent value) { }
	}

""";
		const string Tail = """
}
""";
		return Head + factoryBlock + (additionalTypes.Length == 0 ? string.Empty : "\n" + additionalTypes + "\n") + Tail;
	}

	private static string ToolTree(string method) => """
using ModelContextProtocol.Server;

namespace ModelContextProtocol.Server
{
	[AttributeUsage(AttributeTargets.Method)]
	public sealed class McpServerToolAttribute : Attribute
	{
	}
}

namespace CallGraph
{
	public static class Tools
	{
		[McpServerTool]
""" + method + """
	}
}
""";

	private static Task<ImmutableArray<Diagnostic>> Analyze(string source, string assemblyName = "CallGraph.Roslynk.Plugin", string? additionalTree = null)
	{
		var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
		if (additionalTree is not null)
			trees.Add(CSharpSyntaxTree.ParseText(additionalTree));

		var compilation = CSharpCompilation.Create(
			assemblyName,
			trees,
			CreateReferences(),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
				.WithNullableContextOptions(NullableContextOptions.Enable));
		var analyzer = new PluginApiAnalyzer();

		return compilation
			.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(analyzer))
			.GetAnalyzerDiagnosticsAsync(CancellationToken.None);
	}

	private static ImmutableArray<MetadataReference> CreateReferences()
	{
		string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

		return
		[
			MetadataReference.CreateFromFile(typeof(IPluginContext).Assembly.Location),
			MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Private.CoreLib.dll")),
			MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
			MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Collections.Immutable.dll")),
		];
	}

	private static void AssertSingleDiagnostic(ImmutableArray<Diagnostic> diagnostics, string expectedId)
	{
		string[] ids = [.. diagnostics.Select(diagnostic => diagnostic.Id)];
		Assert.True(
			ids.Count(id => id == expectedId) == 1,
			$"Expected exactly one {expectedId}; diagnostics: [{string.Join(", ", ids)}].");
	}
}
