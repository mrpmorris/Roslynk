using System.Text.RegularExpressions;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The plugin-name rules: at least 3 and at most 32 characters, letters first, then letters or
/// digits. Identity comparisons use the invariant lowercase form.
/// </summary>
/// <remarks>
/// The convention analyzer carries its own copy of the pattern because it must run inside any
/// IDE/SDK that compiles a plugin, where this assembly is absent. The two are version-locked per
/// major — keep them in step when the rules change.
/// </remarks>
public static class PluginNameRules
{
	public const int MinLength = 3;
	public const int MaxLength = 32;

	private static readonly Regex Pattern = new("^[A-Za-z][A-Za-z0-9]{2,31}$", RegexOptions.Compiled);

	public static bool IsValid(string name) =>
		name.Length is >= MinLength and <= MaxLength && Pattern.IsMatch(name);

	public static string Normalize(string name) =>
		name.ToLowerInvariant();
}
