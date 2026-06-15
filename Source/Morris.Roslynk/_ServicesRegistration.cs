using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk;

public static class ServicesRegistration
{
	/// <summary>
	/// Registers Roslynk's engine and feature services. Registrations are added here as each
	/// Infrastructure area and feature slice is built.
	/// </summary>
	public static IServiceCollection AddRoslynk(this IServiceCollection services)
	{
		services.AddSingleton<InstanceRegistry>();
		services.AddSingleton<DiagnosticsService>();
		services.AddSingleton<SymbolResolver>();
		services.AddSingleton<ApplyPipeline>();
		services.AddSingleton<CodeActionService>();
		services.AddSingleton(provider => new SolutionMetrics(RoslynkMeter.Instance, provider.GetRequiredService<InstanceRegistry>()));
		return services;
	}
}
