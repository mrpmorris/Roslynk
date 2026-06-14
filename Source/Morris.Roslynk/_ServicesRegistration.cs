using Microsoft.Extensions.DependencyInjection;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;

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
		return services;
	}
}
