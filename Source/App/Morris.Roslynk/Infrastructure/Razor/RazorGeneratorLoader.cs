using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>
/// Finds the SDK's Razor source generator for a project. The project's own analyzer reference is preferred:
/// it is already shadow-loaded into its own load context (see <see cref="SolutionWorkspace"/>), so using its
/// generator instance costs nothing and runs the exact code the build would. When that reference is refused
/// (the SDK targets a newer Roslyn than Roslynk loads) or the design-time build never registered it, the DLL
/// is loaded into an isolated per-file load context rather than the default one: the default context can
/// hold only one <c>Microsoft.CodeAnalysis.Razor.Compiler</c> per process, so a daemon serving solutions on
/// different SDKs would otherwise fail every one after the first. In that context the host's own assemblies
/// win whatever version is asked for, so the generator binds to the Roslyn Roslynk runs.
/// </summary>
internal static class RazorGeneratorLoader
{
	internal const string RazorCompilerFileName = "Microsoft.CodeAnalysis.Razor.Compiler.dll";
	private const string GeneratorTypeName = "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator";

	private static readonly Lazy<string?> SdkRazorCompilerPath = new(FindRazorCompilerInSdkDirectory);

	// Keyed by path and file stamp, so a rebuilt or reinstalled compiler is loaded afresh.
	private static readonly ConcurrentDictionary<string, ISourceGenerator?> IsolatedGenerators = new(StringComparer.OrdinalIgnoreCase);

	public static bool IsRazorCompiler(AnalyzerReference reference) =>
		reference.FullPath is string path && path.EndsWith(RazorCompilerFileName, StringComparison.OrdinalIgnoreCase);

	/// <summary>The Razor generator for <paramref name="project"/>, or null when none can be found or loaded.</summary>
	public static ISourceGenerator? For(Project project)
	{
		AnalyzerReference? reference = project.AnalyzerReferences.FirstOrDefault(IsRazorCompiler);
		if (reference is not null)
		{
			try
			{
				ISourceGenerator? own = reference.GetGenerators(project.Language).FirstOrDefault(IsRazorGenerator);
				if (own is not null)
					return own;
			}
			catch (Exception)
			{
				// Fall through to the isolated load.
			}
		}

		string? path = reference?.FullPath ?? SdkRazorCompilerPath.Value;
		return path is null
			? null
			: IsolatedGenerators.GetOrAdd(StampedKey(path), _ => LoadIsolated(path));
	}

	private static bool IsRazorGenerator(ISourceGenerator generator) =>
		string.Equals(generator.GetGeneratorType().FullName, GeneratorTypeName, StringComparison.Ordinal);

	private static ISourceGenerator? LoadIsolated(string path)
	{
		try
		{
			var context = new GeneratorLoadContext(System.IO.Path.GetDirectoryName(path)!);
			Assembly assembly = context.LoadFromAssemblyPath(path);
			return assembly.GetType(GeneratorTypeName) is Type type && Activator.CreateInstance(type) is IIncrementalGenerator generator
				? generator.AsSourceGenerator()
				: null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// One Razor compiler and its private dependencies. Every name the host can supply (Roslyn, the BCL) comes
	/// from the default context regardless of the requested version, so types are shared with the workspace;
	/// anything else (the compiler's own utilities) loads from the compiler's directory.
	/// </summary>
	private sealed class GeneratorLoadContext : AssemblyLoadContext
	{
		private readonly string CompilerDirectory;

		public GeneratorLoadContext(string directory)
			: base("Roslynk.RazorGenerator", isCollectible: false)
		{
			CompilerDirectory = directory;
		}

		protected override Assembly? Load(AssemblyName assemblyName)
		{
			if (assemblyName.Name is not string name)
				return null;

			try
			{
				return Default.LoadFromAssemblyName(new AssemblyName(name));
			}
			catch (Exception)
			{
			}

			string candidate = System.IO.Path.Combine(CompilerDirectory, name + ".dll");
			return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
		}
	}

	private static string StampedKey(string path)
	{
		try
		{
			var info = new FileInfo(path);
			if (info.Exists)
				return string.Concat(path, "|", info.LastWriteTimeUtc.Ticks.ToString(), "|", info.Length.ToString());
		}
		catch
		{
		}

		return path;
	}

	private static string? FindRazorCompilerInSdkDirectory()
	{
		try
		{
			var searchRoots = new List<string>();

			if (MSBuildLocator.IsRegistered)
			{
				string msbuildPath = MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault()?.MSBuildPath ?? string.Empty;
				if (msbuildPath.Length > 0)
				{
					searchRoots.Add(System.IO.Path.Combine(msbuildPath, "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators"));
					searchRoots.Add(System.IO.Path.Combine(msbuildPath, "Sdks", "Microsoft.NET.Sdk.Razor", "tools"));
					searchRoots.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(msbuildPath, "..", "..", "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators")));
					searchRoots.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(msbuildPath, "..", "..", "Sdks", "Microsoft.NET.Sdk.Razor", "tools")));
				}
			}

			// Windows Program Files layout and Linux/macOS DOTNET_ROOT / ~/.dotnet / /usr/share/dotnet.
			foreach (string? sdkRoot in SdkRootCandidates())
			{
				if (sdkRoot is null || !Directory.Exists(sdkRoot))
					continue;

				foreach (string versionDir in Directory.EnumerateDirectories(sdkRoot))
				{
					searchRoots.Add(System.IO.Path.Combine(versionDir, "Sdks", "Microsoft.NET.Sdk.Razor", "source-generators"));
					searchRoots.Add(System.IO.Path.Combine(versionDir, "Sdks", "Microsoft.NET.Sdk.Razor", "tools"));
				}
			}

			foreach (string directory in searchRoots)
			{
				string path = System.IO.Path.Combine(directory, RazorCompilerFileName);
				if (File.Exists(path))
					return path;
			}
		}
		catch (Exception)
		{
		}

		return null;
	}

	private static IEnumerable<string?> SdkRootCandidates()
	{
		string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
		if (!string.IsNullOrWhiteSpace(dotnetRoot))
			yield return System.IO.Path.Combine(dotnetRoot, "sdk");

		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		if (!string.IsNullOrWhiteSpace(programFiles))
			yield return System.IO.Path.Combine(programFiles, "dotnet", "sdk");

		string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (!string.IsNullOrWhiteSpace(userProfile))
			yield return System.IO.Path.Combine(userProfile, ".dotnet", "sdk");

		yield return "/usr/share/dotnet/sdk";
		yield return "/usr/local/share/dotnet/sdk";
	}
}
