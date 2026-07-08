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
/// </summary>
internal sealed class ShadowCopyingAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
	private readonly string _shadowRoot;
	private readonly AssemblyLoadContext _loadContext;

	// Original dependency locations, keyed by simple assembly name, used to resolve transitive references.
	private readonly ConcurrentDictionary<string, string> _dependencyPathsByName =
		new(StringComparer.OrdinalIgnoreCase);

	// Maps an original assembly path to the shadow copy that has already been made for it.
	private readonly ConcurrentDictionary<string, string> _shadowCopies =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly object _copyLock = new();

	public ShadowCopyingAnalyzerAssemblyLoader(string shadowRoot)
	{
		// A unique per-instance subdirectory keeps copies from different workspace loads from colliding.
		_shadowRoot = Path.Combine(shadowRoot, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_shadowRoot);

		_loadContext = new AssemblyLoadContext("Roslynk.AnalyzerShadowCopy", isCollectible: false);
		_loadContext.Resolving += ResolveDependency;
	}

	public void AddDependencyLocation(string fullPath)
	{
		string name = Path.GetFileNameWithoutExtension(fullPath);
		if (!string.IsNullOrEmpty(name))
			_dependencyPathsByName[name] = fullPath;
	}

	public Assembly LoadFromPath(string fullPath)
	{
		string shadow = GetOrCreateShadowCopy(fullPath);
		return _loadContext.LoadFromAssemblyPath(shadow);
	}

	private Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName assemblyName)
	{
		if (assemblyName.Name is not string name)
			return null;

		if (!_dependencyPathsByName.TryGetValue(name, out string? originalPath))
			return null;

		string shadow = GetOrCreateShadowCopy(originalPath);
		return context.LoadFromAssemblyPath(shadow);
	}

	private string GetOrCreateShadowCopy(string originalPath)
	{
		if (_shadowCopies.TryGetValue(originalPath, out string? existing))
			return existing;

		lock (_copyLock)
		{
			if (_shadowCopies.TryGetValue(originalPath, out existing))
				return existing;

			if (!File.Exists(originalPath))
			{
				// Nothing to copy; fall back to the original so the loader still has a usable path.
				_shadowCopies[originalPath] = originalPath;
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

			_shadowCopies[originalPath] = destPath;
			return destPath;
		}
	}
}
