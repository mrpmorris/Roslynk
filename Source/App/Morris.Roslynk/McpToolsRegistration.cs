using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Tools;

namespace Morris.Roslynk;

public static class McpToolsRegistration
{
	/// <summary>
	/// Registers every [McpServerToolType] tool in the Roslynk assembly and wraps each one in
	/// <see cref="RoslynkTool"/>. Hosts must use this instead of calling WithToolsFromAssembly directly, so
	/// the published schema and the failure shape are the same everywhere Roslynk is hosted.
	/// </summary>
	public static IMcpServerBuilder WithRoslynkTools(this IMcpServerBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);

		builder.WithToolsFromAssembly(typeof(ServicesRegistration).Assembly);

		IServiceCollection services = builder.Services;
		for (int index = 0; index < services.Count; index++)
		{
			ServiceDescriptor descriptor = services[index];
			if (descriptor.ServiceType != typeof(McpServerTool) || descriptor.IsKeyedService)
				continue;

			services[index] = ServiceDescriptor.Describe(
				typeof(McpServerTool),
				provider => new RoslynkTool(Resolve(provider, descriptor)),
				descriptor.Lifetime);
		}

		return builder;
	}

	private static McpServerTool Resolve(IServiceProvider provider, ServiceDescriptor descriptor) =>
		descriptor.ImplementationInstance is McpServerTool instance
			? instance
			: descriptor.ImplementationFactory is not null
				? (McpServerTool)descriptor.ImplementationFactory(provider)
				: (McpServerTool)ActivatorUtilities.CreateInstance(
					provider,
					descriptor.ImplementationType ?? throw new InvalidOperationException("An McpServerTool registration had no implementation."));
}
