using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Reads a project's target framework. Roslyn's native multi-target expansion, and
/// <see cref="SolutionWorkspace"/>'s fallback expansion, both name additional <see cref="Project"/>
/// instances with a <c>Name(tfm)</c> suffix (e.g. <c>MultiLib(net8.0)</c>). When a project has no such
/// suffix the framework is derived from its C# preprocessor symbols instead, so a natively-expanded
/// per-TFM project is still identified even if it carries no name suffix.
/// </summary>
public static class ProjectFramework
{
	public static string? Of(Project project)
	{
		string name = project.Name;
		int open = name.LastIndexOf('(');
		if (open >= 0 && name.EndsWith(')'))
			return name[(open + 1)..^1];

		return FromParseOptions(project);
	}

	/// <summary>
	/// Derives a project's target framework from its C# preprocessor symbols (e.g. <c>NET8_0</c> -&gt;
	/// <c>net8.0</c>, <c>NETSTANDARD2_0</c> -&gt; <c>netstandard2.0</c>) — the per-TFM identity axis Roslyn's
	/// native multi-target expansion exposes. Returns null for a non-C# project, or a framework with no
	/// <c>NETx_0</c> symbol (e.g. <c>net462</c>), which is instead identified by its name suffix.
	/// </summary>
	public static string? FromParseOptions(Project project)
	{
		if (project.ParseOptions is not CSharpParseOptions csharpOptions)
			return null;

		foreach (string symbol in csharpOptions.PreprocessorSymbolNames)
		{
			if (symbol.StartsWith("NET", StringComparison.Ordinal) &&
				symbol.EndsWith("_0", StringComparison.Ordinal) &&
				symbol.Length > 5)
			{
				string version = symbol[3..^2].ToLowerInvariant();
				return $"net{version}.0";
			}
		}

		return null;
	}

}
