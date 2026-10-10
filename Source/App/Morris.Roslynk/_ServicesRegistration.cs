using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk;

public static class ServicesRegistration
{
	/// <summary>
	/// Registers Roslynk's engine and feature services. Registrations are added here as each
	/// Infrastructure area and feature slice is built. The optional <paramref name="responseBudget"/> caps
	/// every tool result; hosts that read it from configuration pass it here (see the Roslynk MCP host's
	/// ResponseBudgetConfiguration), otherwise the default applies.
	/// </summary>
	public static IServiceCollection AddRoslynk(this IServiceCollection services, ResponseBudget? responseBudget = null)
	{
		services.AddSingleton(responseBudget ?? ResponseBudget.Default);
		services.AddSingleton<InstanceRegistry>();
		services.AddSingleton<DiagnosticsService>();
		services.AddSingleton<SymbolResolver>();
		services.AddSingleton<ProjectionService>();
		services.AddSingleton<ConditionalCoverage>();
		services.AddSingleton<ApplyPipeline>();
		services.AddSingleton<DocumentDiagnosticsProvider>();
		services.AddSingleton<CodeActionService>();
		services.AddSingleton(provider => new SolutionMetrics(RoslynkMeter.Instance, provider.GetRequiredService<InstanceRegistry>()));
		return services;
	}
}
