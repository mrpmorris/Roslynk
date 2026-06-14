using Microsoft.Extensions.DependencyInjection;

namespace Morris.Roslynk;

public static class ServicesRegistration
{
	/// <summary>
	/// Registers Roslynk's engine and feature services. Registrations are added here as each
	/// Infrastructure area and feature slice is built.
	/// </summary>
	public static IServiceCollection AddRoslynk(this IServiceCollection services)
	{
		return services;
	}
}
