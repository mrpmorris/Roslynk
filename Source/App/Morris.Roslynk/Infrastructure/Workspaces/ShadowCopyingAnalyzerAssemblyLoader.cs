using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// An <see cref="IAnalyzerAssemblyLoader"/> that copies analyzer and source-generator assemblies to a
/// temporary directory and loads the copies, so the originals in a project's bin/obj remain unlocked.
/// <para>
/// <see cref="MSBuildWorkspace"/>'s default loader maps analyzer assemblies directly from their build
/// output and holds a file lock for the lifetime of the workspace. Because Roslynk keeps solutions
/// loaded, that lock never releases and a concurrent build cannot overwrite a generator's output DLL.
/// </para>
/// <para>
/// Every requested assembly gets its own <see cref="AssemblyLoadContext"/>, keyed by path and file stamp.
/// A context can hold only one assembly per simple name, so a single shared context silently drops
/// analyzers whenever two projects resolve the same assembly name from different package versions
/// ("assembly with same name is already loaded") — and it can never observe a rebuilt generator, because
/// the first copy loaded for a path is the only one it can ever hold. Per-assembly contexts make name
/// collisions impossible, and stamp-keyed contexts let a solution reload pick up new bits after a rebuild.
/// Dependencies resolve from the default context first (the host's own Roslyn and BCL always win over
/// copies an analyzer package carries beside it), then the assembly's own directory, then locations
/// registered via <see cref="AddDependencyLocation"/>.
/// </para>
/// </summary>
internal sealed class ShadowCopyingAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
	private readonly string _shadowRoot;

	// Original dependency locations, keyed by simple assembly name, used to resolve transitive references.
	private readonly ConcurrentDictionary<string, string> _dependencyPathsByName =
		new(StringComparer.OrdinalIgnoreCase);

	// One load context per stamped assembly path; see class remarks.
	private readonly ConcurrentDictionary<string, AnalyzerLoadContext> _loadContexts =
		new(StringComparer.OrdinalIgnoreCase);

	// Maps a stamped original assembly path to the shadow copy that has already been made for it.
	private readonly ConcurrentDictionary<string, string> _shadowCopies =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly object _copyLock = new();

	public ShadowCopyingAnalyzerAssemblyLoader(string shadowRoot)
	{
		// A unique per-instance subdirectory keeps copies from different workspace loads from colliding.
		_shadowRoot = Path.Combine(shadowRoot, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_shadowRoot);
	}

	public void AddDependencyLocation(string fullPath)
	{
		string name = Path.GetFileNameWithoutExtension(fullPath);
		if (!string.IsNullOrEmpty(name))
			_dependencyPathsByName[name] = fullPath;
	}

	public Assembly LoadFromPath(string fullPath)
	{
		AnalyzerLoadContext loadContext = _loadContexts.GetOrAdd(
			StampedKey(fullPath),
			_ => new AnalyzerLoadContext(this, Path.GetDirectoryName(fullPath) ?? ""));

		return loadContext.LoadFromAssemblyPath(GetOrCreateShadowCopy(fullPath));
	}

	// Identifies a specific version of a file: the same path rebuilt with new content gets a new key,
	// and with it a new load context and shadow copy.
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

	private string GetOrCreateShadowCopy(string originalPath)
	{
		string key = StampedKey(originalPath);
		if (_shadowCopies.TryGetValue(key, out string? existing))
			return existing;

		lock (_copyLock)
		{
			if (_shadowCopies.TryGetValue(key, out existing))
				return existing;

			if (!File.Exists(originalPath))
			{
				// Nothing to copy; fall back to the original so the loader still has a usable path.
				_shadowCopies[key] = originalPath;
				return originalPath;
			}

			// Preserve the file name (analyzers can resolve resources relative to it) under a unique folder.
			string destDir = Path.Combine(_shadowRoot, Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(destDir);
			string destPath = Path.Combine(destDir, Path.GetFileName(originalPath));

			File.Copy(originalPath, destPath, overwrite: true);

			// Copy the matching PDB when present so analyzer stack traces stay meaningful.
			string pdb = Path.ChangeExtension(originalPath, ".pdb");
			if (File.Exists(pdb))
				File.Copy(pdb, Path.ChangeExtension(destPath, ".pdb"), overwrite: true);

			_shadowCopies[key] = destPath;
			return destPath;
		}
	}

	/// <summary>
	/// The context one analyzer assembly (and its private dependencies) loads into. No <c>Load</c>
	/// override: the default context gets first claim on every assembly name, so the host's Roslyn and
	/// BCL always satisfy those references; only names the host cannot supply reach
	/// <see cref="ResolveDependency"/>.
	/// </summary>
	private sealed class AnalyzerLoadContext : AssemblyLoadContext
	{
		private readonly ShadowCopyingAnalyzerAssemblyLoader _owner;
		private readonly string _directory;

		public AnalyzerLoadContext(ShadowCopyingAnalyzerAssemblyLoader owner, string directory)
			: base("Roslynk.AnalyzerShadowCopy", isCollectible: false)
		{
			_owner = owner;
			_directory = directory;
			Resolving += ResolveDependency;
		}

		private Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName assemblyName)
		{
			if (assemblyName.Name is not string name)
				return null;

			// An analyzer package's own dependencies sit beside it in its directory.
			string sameDirectory = Path.Combine(_directory, name + ".dll");
			if (File.Exists(sameDirectory))
				return context.LoadFromAssemblyPath(_owner.GetOrCreateShadowCopy(sameDirectory));

			if (_owner._dependencyPathsByName.TryGetValue(name, out string? registeredPath))
				return context.LoadFromAssemblyPath(_owner.GetOrCreateShadowCopy(registeredPath));

			return null;
		}
	}
}
