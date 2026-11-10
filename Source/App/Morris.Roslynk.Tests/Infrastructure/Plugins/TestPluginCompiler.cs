using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

/// <summary>
/// Compiles a real plugin-shaped DLL to disk so the loader and discovery exercise real metadata.
/// References the test host's managed core set plus the contract assembly, so an emitted DLL is
/// indistinguishable from a built one as far as its PE is concerned.
/// </summary>
public static class TestPluginCompiler
{
	public const string ApiVersionV1Source = """
		[assembly: Morris.Roslynk.Plugins.V1.RoslynkApiVersion(Morris.Roslynk.Plugins.V1.ApiVersion.V1)]
		""";

	/// <summary>Emits <paramref name="source"/> to <paramref name="path"/> and asserts the emit succeeded.</summary>
	public static void Emit(
		string path,
		string? source = null,
		bool contractReference = true,
		bool roslynReference = false,
		string? assemblyName = null,
		IReadOnlyList<string>? extraReferencePaths = null)
	{
		var references = new List<MetadataReference>();
		string? runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
		if (runtimeDirectory is not null)
		{
			// The managed core set only; the runtime directory also holds native DLLs (coreclr,
			// msquic, ...) whose PE images carry no metadata and would fail the emit.
			references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Private.CoreLib.dll")));
			references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")));
		}

		if (contractReference)
			references.Add(MetadataReference.CreateFromFile(typeof(ApiVersion).Assembly.Location));
		if (roslynReference)
		{
			// A direct Microsoft.CodeAnalysis reference so the version-skew check has one to compare.
			// Common, not Workspaces: the emitted reference row must name the assembly the source's
			// own types come from, not one that merely forwards them.
			references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.CodeAnalysis.Location).Assembly.Location));
		}

		if (extraReferencePaths is not null)
		{
			references.AddRange(extraReferencePaths.Select(file => (MetadataReference)MetadataReference.CreateFromFile(file)));
		}

		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		using var stream = File.Create(path);
		var emitResult = CSharpCompilation.Create(
			assemblyName ?? System.IO.Path.GetFileNameWithoutExtension(path),
			[CSharpSyntaxTree.ParseText(source ?? ApiVersionV1Source)],
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
			.Emit(stream);
		Assert.True(emitResult.Success, string.Join("; ", emitResult.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.GetMessage())));
	}
}
