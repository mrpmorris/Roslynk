using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins.V1;

/// <summary>
/// The contract every Roslynk plugin implements. The plugin's lifetime is the host's lifetime:
/// <see cref="IObserver{TSelf}.OnCompleted"/>, fired only at host shutdown, is its final callback,
/// and the process exit that follows reclaims everything it holds. There is no IDisposable on the contract.
/// </summary>
public interface IRoslynkPlugin : IObserver<ISolutionEvent>
{
}
