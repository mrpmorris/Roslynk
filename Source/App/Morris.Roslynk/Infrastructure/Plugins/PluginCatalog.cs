using System.Reflection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The plugins found in a plugins folder, each in a subfolder of its own named after its assembly
/// (<c>plugins/Contoso.CallGraph/Contoso.CallGraph.dll</c>, with its dependencies beside it). A folder that
/// cannot be loaded is reported in <see cref="Errors"/> and skipped; it does not stop the others or the daemon.
/// </summary>
public sealed class PluginCatalog
{
	public static readonly PluginCatalog Empty = new([], []);

	public PluginCatalog(IReadOnlyList<LoadedPlugin> plugins, IReadOnlyList<string> errors)
	{
		Plugins = plugins;
		Errors = errors;
	}

	public IReadOnlyList<LoadedPlugin> Plugins { get; }

	/// <summary>Why each plugin folder that was skipped could not be loaded, as <c>&lt;name&gt;: &lt;reason&gt;</c>.</summary>
	public IReadOnlyList<string> Errors { get; }

	/// <summary>The plugin named <paramref name="name"/> (its folder's name, any case), or null.</summary>
	public LoadedPlugin? Find(string name) =>
		Plugins.FirstOrDefault(plugin => string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase));

	/// <summary>The plugins in <paramref name="pluginsPath"/>; none when the folder does not exist.</summary>
	public static PluginCatalog Load(string pluginsPath)
	{
		string[] folders;
		try
		{
			if (!Directory.Exists(pluginsPath))
				return Empty;

			folders = Directory.GetDirectories(pluginsPath);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return new PluginCatalog([], [$"The plugins folder '{pluginsPath}' could not be read: {exception.Message}"]);
		}

		var plugins = new List<LoadedPlugin>();
		var errors = new List<string>();

		foreach (string folder in folders.Order(StringComparer.Ordinal))
		{
			string name = Path.GetFileName(folder);
			try
			{
				plugins.Add(LoadOne(name, folder));
			}
			catch (Exception exception)
			{
				// A plugin's own code (a constructor, a static initializer) can throw anything; none of it may stop the daemon.
				errors.Add($"{name}: {Reason(exception)}");
			}
		}

		return new PluginCatalog(plugins, errors);
	}

	private static LoadedPlugin LoadOne(string name, string folder)
	{
		if (!IsValidName(name))
			throw new InvalidOperationException("A plugin folder's name may contain only letters, digits, '.', '-' and '_'.");

		string assemblyPath = Path.Combine(folder, name + ".dll");
		if (!File.Exists(assemblyPath))
			throw new InvalidOperationException($"No {name}.dll in its folder.");

		Assembly assembly = new PluginLoadContext(assemblyPath).LoadFromAssemblyPath(assemblyPath);
		if (PluginLoadContext.CheckSharedVersions(assembly) is string versionProblem)
			throw new InvalidOperationException(versionProblem);

		Type[] types = assembly.GetExportedTypes();

		IRoslynkPlugin[] instances = types
			.Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IRoslynkPlugin).IsAssignableFrom(type))
			.Select(type => (IRoslynkPlugin)(Activator.CreateInstance(type)
				?? throw new InvalidOperationException($"{type.FullName} could not be created.")))
			.ToArray();

		Type[] toolTypes = types
			.Where(type => type is { IsClass: true } && type.IsDefined(typeof(McpServerToolTypeAttribute), inherit: false))
			.ToArray();

		if (instances.Length == 0 && toolTypes.Length == 0)
			throw new InvalidOperationException($"{name}.dll has no public {nameof(IRoslynkPlugin)} and no MCP tool types.");

		return new LoadedPlugin(name, assembly, instances, toolTypes);
	}

	/// <summary>The plugin's own message, not the reflection wrapper's.</summary>
	internal static string Reason(Exception exception) =>
		exception switch
		{
			TargetInvocationException { InnerException: Exception inner } => Reason(inner),
			TypeInitializationException { InnerException: Exception inner } => Reason(inner),
			ReflectionTypeLoadException { LoaderExceptions: [Exception first, ..] } => Reason(first),
			_ => exception.Message,
		};

	private static bool IsValidName(string name) =>
		name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}

/// <summary>
/// One loaded plugin: the <see cref="IRoslynkPlugin"/> instances the daemon runs, and the MCP tool types it
/// serves on the plugin's own endpoint.
/// </summary>
public sealed record LoadedPlugin(
	string Name,
	Assembly Assembly,
	IReadOnlyList<IRoslynkPlugin> Instances,
	IReadOnlyList<Type> ToolTypes);
