using System.Reflection;
using Morris.Roslynk.Plugins.V1;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// Loads a discovered plugin candidate for execution and finds its factory method.
///
/// The supported API-version range is defined here, at the top of the loader class; discovery
/// receives it from <see cref="MinSupportedApiVersion"/> / <see cref="MaxSupportedApiVersion"/> so
/// there is one place to widen when a new major ships.
/// </summary>
public sealed class PluginLoader
{
	public const ApiVersion MinSupportedApiVersion = ApiVersion.V1;
	public const ApiVersion MaxSupportedApiVersion = ApiVersion.V1;

	private readonly string? RuntimeDirectory;

	public PluginLoader(string? runtimeDirectory = null)
	{
		RuntimeDirectory = runtimeDirectory ?? AppContext.BaseDirectory;
	}

	/// <summary>
	/// Loads the candidate's entry assembly. A failure is reported, never thrown; the folder stays
	/// in place and the next start re-examines it.
	/// </summary>
	public PluginLoadResult Load(PluginCandidate candidate)
	{
		var loadContext = new PluginLoadContext(candidate.PluginName, candidate.EntryAssemblyPath, RuntimeDirectory);
		try
		{
			Assembly assembly = loadContext.LoadFromAssemblyPath(candidate.EntryAssemblyPath);
			return PluginLoadResult.Succeeded(candidate.PluginName, assembly, loadContext);
		}
		catch (BadImageFormatException exception)
		{
			return PluginLoadResult.Failed(candidate.PluginName, PluginDiscoveryStage.Malformed, $"The entry assembly could not be loaded: {exception.Message}");
		}
		catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException)
		{
			return PluginLoadResult.Failed(candidate.PluginName, PluginDiscoveryStage.UnreadableOrLocked, $"The entry assembly could not be loaded: {exception.Message}");
		}
	}

	/// <summary>
	/// Finds the single [RoslynkPluginFactory] method in the loaded assembly. Types that fail to
	/// load are tolerated — except the one declaring the factory, whose absence is a hard failure.
	/// </summary>
	public PluginFactoryResult FindFactory(PluginLoadResult loadResult)
	{
		if (!loadResult.IsSuccess)
			return PluginFactoryResult.Failed(loadResult.PluginName, loadResult.Stage!.Value, loadResult.Reason!);

		Assembly assembly = loadResult.Assembly!;
		MethodInfo? factoryMethod = null;

		IEnumerable<Type> loadableTypes = EnumerateLoadableTypes(assembly, out IReadOnlyList<string> unloadedTypeNames);
		Type? factoryReturnType = null;
		ParameterInfo[]? factoryParameters = null;
		int unreachableFactories = 0;

		foreach (Type type in loadableTypes)
		{
			MethodInfo[] methods;
			try
			{
				methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
			}
			catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
			{
				// A type whose members reference a missing assembly cannot offer an invokable factory.
				continue;
			}

			foreach (MethodInfo method in methods)
			{
				if (!method.IsDefined(typeof(RoslynkPluginFactoryAttribute), inherit: false))
					continue;

				if (factoryMethod is not null)
					return PluginFactoryResult.Failed(loadResult.PluginName, PluginDiscoveryStage.FactoryFailure, $"{loadResult.PluginName} declares more than one [RoslynkPluginFactory] method");

				if (!TryGetFactoryShape(method, out Type? returnType, out ParameterInfo[]? parameters))
				{
					// The attribute names a factory the host could never invoke; its signature
					// references an assembly that is not deployed.
					unreachableFactories++;
					continue;
				}

				factoryMethod = method;
				factoryReturnType = returnType;
				factoryParameters = parameters;
			}
		}

		if (factoryMethod is null)
		{
			return unloadedTypeNames.Count > 0 || unreachableFactories > 0
				// A failed type load is only fatal when it could be hiding the factory.
				? PluginFactoryResult.Failed(loadResult.PluginName, PluginDiscoveryStage.DependencyUnresolved, $"{loadResult.PluginName} declares no [RoslynkPluginFactory] method the loader could reach; some types failed to load ({string.Join("; ", unloadedTypeNames)})")
				: PluginFactoryResult.Failed(loadResult.PluginName, PluginDiscoveryStage.FactoryFailure, $"{loadResult.PluginName} declares no [RoslynkPluginFactory] method");
		}

		if (!IsPubliclyAccessible(factoryMethod.DeclaringType))
			return PluginFactoryResult.Failed(loadResult.PluginName, PluginDiscoveryStage.FactoryFailure, $"The [RoslynkPluginFactory] method '{factoryMethod.Name}' must be declared in a public class, with public containing types");

		if (factoryReturnType != typeof(IRoslynkPlugin))
			return PluginFactoryResult.Failed(loadResult.PluginName, PluginDiscoveryStage.FactoryFailure, $"The [RoslynkPluginFactory] method '{factoryMethod.Name}' must return nullable {nameof(IRoslynkPlugin)}");

		if (factoryParameters!.Length != 1 || factoryParameters[0].ParameterType != typeof(IPluginContext))
			return PluginFactoryResult.Failed(loadResult.PluginName, PluginDiscoveryStage.FactoryFailure, $"The [RoslynkPluginFactory] method '{factoryMethod.Name}' must accept exactly one {nameof(IPluginContext)} parameter");

		return PluginFactoryResult.Succeeded(loadResult.PluginName, factoryMethod);
	}

	/// <summary>True when the type and every type declaring it are public — the analyzer's RK0003 rule, re-checked at load time because a manually deployed assembly never ran the analyzer.</summary>
	private static bool IsPubliclyAccessible(Type? type)
	{
		for (Type? current = type; current is not null; current = current.DeclaringType)
		{
			if (!current.IsPublic && !current.IsNestedPublic)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Reads a method's return type and parameters. A method whose signature references an assembly
	/// that is not deployed cannot materialize its signature, and cannot be invoked by the host
	/// either, so it does not qualify as the factory.
	/// </summary>
	private static bool TryGetFactoryShape(MethodInfo method, out Type? returnType, out ParameterInfo[]? parameters)
	{
		try
		{
			returnType = method.ReturnType;
			parameters = method.GetParameters();
			return true;
		}
		catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
		{
			returnType = null;
			parameters = null;
			return false;
		}
	}

	private static IEnumerable<Type> EnumerateLoadableTypes(Assembly assembly, out IReadOnlyList<string> unloadedTypeNames)
	{
		var loadable = new List<Type>();
		var unloaded = new List<string>();
		try
		{
			loadable.AddRange(assembly.GetTypes());
		}
		catch (ReflectionTypeLoadException exception)
		{
			foreach (Type? type in exception.Types)
			{
				if (type is not null)
					loadable.Add(type);
			}

			unloaded.AddRange(exception
				.LoaderExceptions
				.Select(loaderException => loaderException?.Message ?? "unknown load failure"));
		}

		unloadedTypeNames = unloaded;
		return loadable;
	}
}
