using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using ModelContextProtocol.Server;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

/// <summary>Plugins compiled from source into a plugins folder of the test's own, as a plugin author would build them.</summary>
internal static class TestPlugins
{
	public static string NewPluginsFolder()
	{
		string folder = Path.Combine(Path.GetTempPath(), "roslynk-tests", Guid.NewGuid().ToString("N"), "plugins");
		Directory.CreateDirectory(folder);
		return folder;
	}

	/// <summary>Compiles <paramref name="source"/> to <c>&lt;pluginsFolder&gt;/&lt;name&gt;/&lt;name&gt;.dll</c>.</summary>
	public static void Write(string pluginsFolder, string name, string source) =>
		WriteAssembly(Path.Combine(pluginsFolder, name), name, source);

	/// <summary>
	/// Compiles <paramref name="source"/> to <c>&lt;folder&gt;/&lt;assemblyName&gt;.dll</c>, referencing the
	/// assemblies in <paramref name="alsoReferencing"/> as well as the daemon's.
	/// </summary>
	public static string WriteAssembly(string folder, string assemblyName, string source, params string[] alsoReferencing)
	{
		Directory.CreateDirectory(folder);

		CSharpCompilation compilation = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source)],
			References().Concat(alsoReferencing.Select(path => MetadataReference.CreateFromFile(path))),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		string path = Path.Combine(folder, assemblyName + ".dll");
		EmitResult result = compilation.Emit(path);
		Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
		return path;
	}

	private static IEnumerable<MetadataReference> References()
	{
		string[] trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
		string[] contract = [typeof(IRoslynkPlugin).Assembly.Location, typeof(McpServerToolTypeAttribute).Assembly.Location];
		return trusted.Concat(contract)
			.DistinctBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
			.Select(path => MetadataReference.CreateFromFile(path));
	}
}
