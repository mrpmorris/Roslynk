using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Tests.Infrastructure.Plugins;

/// <summary>
/// Compiles real plugin-shaped DLLs to disk so the loader, the discovery and the load context
/// exercise real metadata. References the test host's managed core set plus the contract assembly,
/// so an emitted DLL is indistinguishable from a built one as far as its PE is concerned. A stub
/// contract (same assembly and type names, no generic constraint) lets a test author plugins the
/// compiler would reject against the real contract — exactly the "never ran a compiler check"
/// assemblies the loader's guards exist for.
/// </summary>
public static class TestPluginCompiler
{
	public const string ValidPluginSource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }
		}
		""";

	public const string MissingAttributeSource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }
		}
		""";

	public const string TwoAttributesSource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]
		[assembly: PluginFactory<SecondFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class SecondFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }
		}
		""";

	/// <summary>The stub contract: the same assembly and type names as Morris.Roslynk.Plugins.Abstractions,
	/// but its attribute allows multiple applications, so a DLL carrying two claims can be produced — the
	/// real compiler rejects them before the emit. Every type rebinds by name to the real contract at
	/// load time, so the two claims resolve against the real attribute and the loader's scan sees them.</summary>
	public const string StubContractAllowingTwoClaimsSource = """
		namespace Morris.Roslynk.Plugins.Abstractions
		{
			public interface IPlugin
			{
				void OnCompleted();
				void OnError(System.Exception error);
				void OnNext(ISolutionEvent value);
			}

			public interface IPluginContext { }

			public interface ISolutionEvent { }

			public interface IPluginFactory
			{
				static abstract IPlugin? Create(IPluginContext context);
			}

			[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
			public sealed class PluginFactoryAttribute<T> : System.Attribute
			{
			}
		}
		""";

	public const string ExplicitImplementationSource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			static IPlugin? IPluginFactory.Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }
		}
		""";

	public const string InternalFactorySource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		internal sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }
		}
		""";

	public const string NullReturnSource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => null;
		}
		""";

	public const string ThrowingFactorySource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => throw new InvalidOperationException("the plugin's own initialization failed");
		}
		""";

	/// <summary>A plugin whose factory's base type comes from a dependency the deployment does not carry.</summary>
	public const string MissingDependencySource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : SomeLibrary.Base, IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => null;
		}
		""";

	/// <summary>A factory that oversleeps any small startup budget; the budget is a stopwatch, not a kill switch, so the call completes before the next checkpoint fires.</summary>
	public const string SlowFactorySource = """
		using System.Threading;
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context)
			{
				Thread.Sleep(400);
				return new Plugin();
			}
		}

		public sealed class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }
		}
		""";

	/// <summary>A factory that fails with the identity of the Code.CodeAnalysis assembly it bound, so a test can assert which copy the load context resolved.</summary>
	public const string CodeAnalysisProbeSource = """
		using System;
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) =>
				throw new InvalidOperationException(typeof(Microsoft.CodeAnalysis.Solution).Assembly.FullName!);
		}
		""";

	/// <summary>A plugin with two tools; the second is an overload of the first, so the duplicate-exposed-name rule fires.</summary>
	public const string ToolPluginSource = """
		using System.Threading;
		using System;
		using Morris.Roslynk.Plugins.Abstractions;
		using ModelContextProtocol.Server;

		[assembly: PluginFactory<PluginFactory>]

		public sealed class PluginFactory : IPluginFactory
		{
			public static IPlugin? Create(IPluginContext context) => new Plugin();
		}

		public class Plugin : IPlugin
		{
			public void OnCompleted() { }
			public void OnError(Exception error) { }
			public void OnNext(ISolutionEvent value) { }

			[McpServerTool]
			public string First(string solutionId, CancellationToken cancellationToken) => "first:" + solutionId;

			[McpServerTool]
			public string First(int overload) => "overload";

			[McpServerTool]
			public string Second(string solutionId, CancellationToken cancellationToken) => "second";
		}

		public class Derived : Plugin
		{
			[McpServerTool]
			public string Inherited(string solutionId, CancellationToken cancellationToken) => "inherited";
		}
		""";

	/// <summary>Emits <paramref name="source"/> to `{directory}/{assemblyName}.dll` and asserts the emit succeeded.</summary>
	public static string Emit(
		string directory,
		string assemblyName,
		string source,
		bool contractReference = true,
		bool toolReference = false,
		bool roslynReference = false,
		bool loggingReference = false,
		IReadOnlyList<(string AssemblyName, string Source)>? extraAssemblies = null,
		IReadOnlyList<string>? extraReferencePaths = null,
		string? assemblyIdentity = null)
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
		{
			// A stub contract replaces the real one when a test needs a claim the real compiler would
			// reject; both carry the same simple name and cannot be imported together.
			bool stubProvidesContract = extraAssemblies?.Any(assembly => assembly.AssemblyName == typeof(IPluginContext).Assembly.GetName().Name) == true;
			if (!stubProvidesContract)
				references.Add(MetadataReference.CreateFromFile(typeof(IPluginContext).Assembly.Location));
		}
		if (toolReference)
		{
			references.Add(MetadataReference.CreateFromFile(typeof(McpServerToolAttribute).Assembly.Location));
			references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location));
		}
		if (roslynReference)
			references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.CodeAnalysis.Solution).Assembly.Location));
		if (loggingReference)
			references.Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.Logging.ILogger).Assembly.Location));

		if (extraAssemblies is not null)
		{
			foreach ((string name, string extraSource) in extraAssemblies)
			{
				using var stubStream = new MemoryStream();
				var stubResult = CSharpCompilation.Create(
					name,
					[CSharpSyntaxTree.ParseText(extraSource)],
					references,
					new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(stubStream);
				Assert.True(stubResult.Success, string.Join("; ", stubResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())));
				stubStream.Position = 0;
				references.Add(MetadataReference.CreateFromImage(stubStream.ToArray()));
			}
		}

		if (extraReferencePaths is not null)
			references.AddRange(extraReferencePaths.Select(file => MetadataReference.CreateFromFile(file)));

		Directory.CreateDirectory(directory);
		string path = Path.Combine(directory, $"{assemblyIdentity ?? assemblyName}.dll");
		using var stream = File.Create(path);
		var emitResult = CSharpCompilation.Create(
			assemblyName,
			[CSharpSyntaxTree.ParseText(source)],
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
			.Emit(stream);
		Assert.True(emitResult.Success, string.Join("; ", emitResult.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.GetMessage())));
		return path;
	}

	/// <summary>
	/// The stub contract: the same assembly and type names as Morris.Roslynk.Plugins.Abstractions, but
	/// its attribute carries no generic constraint, so a plugin claiming a factory that does not
	/// implement the host's IPluginFactory still compiles. Pair it with <see cref="ForeignFactoryClaimSource"/>
	/// so the loader's assignability guard sees exactly what an assembly that never ran a compiler
	/// check would produce.
	/// </summary>
	public const string StubContractSource = """
		namespace Morris.Roslynk.Plugins.Abstractions
		{
			[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = false)]
			public sealed class PluginFactoryAttribute<T> : System.Attribute
			{
			}
		}
		""";

	/// <summary>A factory claiming an interface of its own, compiled against the stub attribute so the
	/// claim the compiler would reject emits anyway; the loader's assignability guard must fire.</summary>
	public const string ForeignFactoryClaimSource = """
		using Morris.Roslynk.Plugins.Abstractions;

		[assembly: PluginFactory<PluginFactory>]

		namespace Foreign
		{
			public interface IPluginFactory
			{
			}
		}

		public sealed class PluginFactory : Foreign.IPluginFactory
		{
			public static object? Create(Foreign.IPluginFactory context) => null;
		}
		""";

	/// <summary>A stub whose factory returns an object — reachable only through hand-written or
	/// unconstrained builds, exercising the loader's instance guard family.</summary>
	public const string StubContractWithObjectFactorySource = """
		namespace Morris.Roslynk.Plugins.Abstractions
		{
			public interface IPluginContext { }

			public interface IPluginFactory
			{
				static abstract object? Create(IPluginContext context);
			}

			[System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = false)]
			public sealed class PluginFactoryAttribute<T> : System.Attribute
			{
			}
		}
		""";

	/// <summary>A helper base type deployed as a separate assembly, so a plugin that references it but
	/// deploys without it fails its load with a missing dependency.</summary>
	public const string MissingDependencyLibrarySource = """
		namespace SomeLibrary
		{
			public abstract class Base
			{
			}
		}
		""";
}
