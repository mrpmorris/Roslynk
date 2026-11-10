namespace Morris.Roslynk.Plugins.V1;

/// <summary>
/// Marks the plugin's factory method. Each plugin entry assembly exposes exactly one method
/// decorated with this attribute; the convention analyzer enforces its shape at build time.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RoslynkPluginFactoryAttribute : Attribute
{
}
