using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Infrastructure.Plugins;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Middleware that holds MCP requests until plugin startup has completed: the daemon's port answers
/// and its endpoints respond during the startup window, but no MCP request is accepted before every
/// candidate plugin has loaded (each bounded by its startup budget, so the wait is finite). The
/// daemon endpoints — <c>/version</c>, <c>/shutdown</c>, <c>/restart</c>, <c>/plugins</c> — are
/// exempt, so the stdio bridge's handshake and the plugin CLI work during the window.
/// </summary>
public static class PluginStartupGateExtensions
{
	public static void UsePluginStartupGate(this WebApplication app)
	{
		app.Use(async (context, next) =>
		{
			if (!DaemonEndpoints.IsDaemonEndpoint(context.Request.Path))
			{
				PluginStartupGate gate = context.RequestServices.GetRequiredService<PluginStartupGate>();
				await gate.WaitAsync(context.RequestAborted);
			}

			await next(context);
		});
	}
}
