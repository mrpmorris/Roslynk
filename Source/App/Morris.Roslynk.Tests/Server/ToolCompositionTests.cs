using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Morris.Roslynk.Features.CodeActions.ApplyCodeFix;
using Morris.Roslynk.Features.Usings.RemoveUnusedUsings;

namespace Morris.Roslynk.Tests.Server;

public class ToolCompositionTests
{
	[Fact]
	public void WhenTheServerIsComposedAsTheHostDoes_ThenTheToolsResolve()
	{
		// The tools that drive fixes take the analyzer-aware diagnostics provider, so a missing registration
		// would only show up when a tool is first invoked.
		using ServiceProvider provider = Build();

		Assert.NotNull(provider.GetRequiredService<ApplyCodeFixTool>());
		Assert.NotNull(provider.GetRequiredService<RemoveUnusedUsingsTool>());
	}

	[Fact]
	public void WhenTheServerIsComposedAsTheHostDoes_ThenTheToolSurfaceIsExposed()
	{
		using ServiceProvider provider = Build();

		HashSet<string> toolNames = provider.GetServices<McpServerTool>()
			.Select(tool => tool.ProtocolTool.Name)
			.ToHashSet(StringComparer.Ordinal);

		Assert.Contains(ApplyCodeFixTool.ApplyCodeFixName, toolNames);
		Assert.Contains(RemoveUnusedUsingsTool.RemoveUnusedUsingsName, toolNames);
	}

	private static ServiceProvider Build()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		services.AddSingleton<ApplyCodeFixTool>();
		services.AddSingleton<RemoveUnusedUsingsTool>();
		return services.BuildServiceProvider();
	}
}
