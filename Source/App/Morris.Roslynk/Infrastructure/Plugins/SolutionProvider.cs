using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Plugins;
using EngineStatus = Morris.Roslynk.Infrastructure.Lifecycle.SolutionStatus;
using PluginStatus = Morris.Roslynk.Plugins.SolutionStatus;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The plugins' view of <see cref="InstanceRegistry"/>: <c>open_solution</c>'s open-and-wait, then the
/// published model, and nothing else of the registry or an instance's lifecycle.
/// </summary>
public sealed class SolutionProvider : ISolutionProvider
{
	private readonly InstanceRegistry InstanceRegistry;

	public SolutionProvider(InstanceRegistry instanceRegistry)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
	}

	public async Task<GetModelResponse> GetModelAsync(string solutionId, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(solutionId);

		string fullPath = Path.GetFullPath(solutionId);
		if (!File.Exists(fullPath))
			return new GetModelResponse(Guid.Empty, PluginStatus.Faulted, Model: null, $"No solution file was found at '{fullPath}'.");

		// GetOrBeginAsync touches the instance, so a plugin's reads count as use for idle eviction.
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(fullPath, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);

		return new GetModelResponse(
			model.Id,
			ToPluginStatus(model.Status),
			model.Solution,
			model.Status == EngineStatus.Faulted ? model.FaultMessage ?? "The solution failed to load." : null);
	}

	private static PluginStatus ToPluginStatus(EngineStatus status) =>
		status switch
		{
			EngineStatus.Building => PluginStatus.Building,
			EngineStatus.Updating => PluginStatus.Updating,
			EngineStatus.Ready => PluginStatus.Ready,
			EngineStatus.Faulted => PluginStatus.Faulted,
			_ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
		};
}
