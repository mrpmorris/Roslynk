namespace Morris.Roslynk.Plugins.Abstractions;

/// <summary>
/// The assembly-level attribute that claims a plugin's factory. The type argument implements
/// <see cref="IPluginFactory"/> — enforced by the generic constraint, so a wrong factory type or a
/// wrong <c>Create</c> shape is a compile error at the point of claim; the argument is mandatory, so
/// an omitted type argument is a compile error too.
/// </summary>
/// <typeparam name="T">The plugin's factory type.</typeparam>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class PluginFactoryAttribute<T> : Attribute where T : IPluginFactory
{
}
