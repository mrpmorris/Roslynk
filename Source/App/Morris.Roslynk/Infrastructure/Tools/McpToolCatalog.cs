using System.Reflection;

namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>Descriptions for every MCP tool in the Roslynk assembly.</summary>
public static class McpToolCatalog
{
	public static readonly Dictionary<string, string> Descriptions = LoadDescriptions();

	private static Dictionary<string, string> LoadDescriptions()
	{
		Assembly assembly = typeof(IMcpTool).Assembly;
		return assembly
			.GetTypes()
			.Where(type => type.IsClass && !type.IsAbstract && typeof(IMcpTool).IsAssignableFrom(type))
			.ToDictionary(
				type => Invoke(type, nameof(IMcpTool.GetName)),
				type => Invoke(type, nameof(IMcpTool.GetDescription)),
				StringComparer.OrdinalIgnoreCase);
	}

	private static string Invoke(Type type, string methodName) =>
		(string)(type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null)
			?? throw new InvalidOperationException($"'{type.FullName}' does not provide {methodName}()."));
}
