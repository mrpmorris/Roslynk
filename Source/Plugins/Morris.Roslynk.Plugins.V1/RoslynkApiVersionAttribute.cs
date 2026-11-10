namespace Morris.Roslynk.Plugins.V1;

/// <summary>
/// Assembly-level metadata stamped by the package's build targets. The loader reads it with a
/// reflection-only probe — without executing any plugin code — to decide whether the plugin's
/// contract major is supported.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RoslynkApiVersionAttribute : Attribute
{
	/// <summary>The contract major the assembly was built against.</summary>
	public ApiVersion Version { get; }

	public RoslynkApiVersionAttribute(ApiVersion version) => Version = version;
}
