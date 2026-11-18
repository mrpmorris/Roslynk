using Morris.Roslynk.Plugins.Abstractions;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The host-internal dispatch shim: the only invocation path for plugin factories. Reflection is
/// used only to choose <typeparamref name="T"/> (<see cref="System.Reflection.MethodInfo.MakeGenericMethod"/>);
/// inside the shim, <typeparamref name="T"/>.Create is ordinary generic dispatch — the same compiler
/// mechanism that rejects wrong shapes at build time binds the correct implementation at run time.
/// This covers implicit and explicit interface implementations (which metadata names
/// IPluginFactory.Create, invisible to a name-based GetMethod), internal factory classes, and other
/// Create overloads on the factory class. No name-based method lookup exists anywhere in the load path.
/// </summary>
internal static class PluginDispatcher
{
	internal static IPlugin? Dispatch<T>(IPluginContext context) where T : IPluginFactory =>
		T.Create(context);
}
