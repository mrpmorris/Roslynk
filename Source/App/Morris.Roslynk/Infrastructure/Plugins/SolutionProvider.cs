using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Plugins;

namespace Morris.Roslynk.Infrastructure.Plugins;

/// <summary>
/// The plugins' view of <see cref="InstanceRegistry"/>: the data tools' open-and-wait, then the published model,
/// and nothing else of the registry or an instance's lifecycle. Failures come back as a faulted response, as the
/// tools report them, so one bad read does not end a plugin's run.
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

		try
		{
			string fullPath = Path.GetFullPath(solutionId);
			if (!File.Exists(fullPath))
				return Faulted($"No solution file was found at '{fullPath}'.");

			// GetOrBeginAsync touches the instance, so a plugin's reads count as use for idle eviction.
			RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(fullPath, cancellationToken);
			SolutionModel model = await instance.ReadModelAsync(cancellationToken);

			return new GetModelResponse(
				model.Id,
				model.Status,
				model.Solution,
				model.Status == SolutionStatus.Faulted ? model.FaultMessage ?? "The solution failed to load." : null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception) when (exception is ObjectDisposedException or OperationCanceledException)
		{
			// Not the caller's cancellation: the instance was closed (idle eviction, a reload) during the read.
			return Faulted("The solution was closed while it was being read.");
		}
		catch (Exception exception)
		{
			return Faulted(exception.Message);
		}
	}

	private static GetModelResponse Faulted(string message) =>
		new(Guid.Empty, SolutionStatus.Faulted, Model: null, message);
}
