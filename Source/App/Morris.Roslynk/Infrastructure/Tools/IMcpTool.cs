namespace Morris.Roslynk.Infrastructure.Tools;

/// <summary>Provides the MCP tool name and full usage description.</summary>
public interface IMcpTool
{
	static abstract string GetName();
	static abstract string GetDescription();
}
