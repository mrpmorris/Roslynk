using System.ComponentModel;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Morris.Roslynk.Plugins.Abstractions;

[assembly: PluginFactory<SamplePlugin.Roslynk.Plugin.PluginFactory>]

namespace SamplePlugin.Roslynk.Plugin;

/// <summary>
/// The sample plugin's factory: creates the plugin instance, optionally configuring private
/// dependencies first. The assembly-level attribute above is the plugin's factory claim; the
/// static abstract member on IPluginFactory makes a wrong shape a compile error.
/// </summary>
public sealed class PluginFactory : IPluginFactory
{
	public static IPlugin? Create(IPluginContext context)
	{
		ILogger logger = context.LoggerFactory.CreateLogger("SamplePlugin");
		logger.LogInformation("SamplePlugin created; response budget {MaxResponseChars} characters.", context.MaxResponseChars);
		return new SamplePlugin(context);
	}
}

/// <summary>
/// The plugin itself. Its lifetime is the host's lifetime: OnCompleted, fired only at host
/// shutdown, is the final callback, and there is no IDisposable to release.
/// </summary>
public sealed class SamplePlugin : IPlugin
{
	private readonly IPluginContext Context;

	public SamplePlugin(IPluginContext context)
	{
		Context = context;
		context.LoggerFactory.CreateLogger("SamplePlugin").LogInformation("SamplePlugin created.");
	}

	public void OnCompleted() =>
		Context.LoggerFactory.CreateLogger("SamplePlugin").LogInformation("SamplePlugin: host shutdown.");

	public void OnError(Exception error) =>
		// Never called: the host catches observer exceptions itself; this is the unused IObserver channel.
		Context.LoggerFactory.CreateLogger("SamplePlugin").LogError(error, "SamplePlugin: OnError was called.");

	public void OnNext(ISolutionEvent value) =>
		Context.LoggerFactory.CreateLogger("SamplePlugin").LogInformation("SamplePlugin: solution {SolutionId} is {Kind}.", value.SolutionId, value.Kind);

	/// <summary>
	/// The sample tool. Every plugin tool declares string solutionId first and a CancellationToken
	/// parameter (analyzer rule RKPL004), and reaches its solution through UseSolutionAsync — the
	/// means by which a plugin declares and refreshes its interest in a solution.
	/// </summary>
	[McpServerTool(ReadOnly = true, Title = "SamplePlugin project count")]
	[Description("Counts the projects in a solution, as a sample plugin tool.")]
	public async Task<string> CountProjects(string solutionId, CancellationToken cancellationToken)
	{
		IUseSolutionResult<int> result = await Context.UseSolutionAsync(
			solutionId,
			(solutionContext, token) => ValueTask.FromResult(solutionContext.Solution.Projects.Count()),
			cancellationToken);

		if (!result.IsSuccess)
			return (result.State, result.FaultMessage) switch
			{
				(UseSolutionResultState.NotFound, _) => "error=NotFound",
				(_, { } message) => $"error=Faulted errorMessage={message}",
				_ => "error=Faulted",
			};

		return $"projects={result.Result}";
	}

	/// <summary>A second tool exercising the shared diagnostics pipeline.</summary>
	[McpServerTool(ReadOnly = true, Title = "SamplePlugin diagnostic count")]
	[Description("Counts the compiler, analyzer and Razor diagnostics of a solution, as a sample plugin tool.")]
	public async Task<string> CountDiagnostics(string solutionId, CancellationToken cancellationToken)
	{
		IUseSolutionResult<string> result = await Context.UseSolutionAsync(
			solutionId,
			async (solutionContext, token) =>
			{
				ImmutableArray<Diagnostic> diagnostics = await solutionContext.GetDiagnosticsAsync(token);
				return $"diagnostics={diagnostics.Length}";
			},
			cancellationToken);

		return result.IsSuccess ? result.Result : "error=Faulted";
	}
}
