using System.Diagnostics;

namespace Morris.Roslynk.Infrastructure.Observability;

/// <summary>
/// The single <see cref="ActivitySource"/> all Roslynk spans are emitted from.
/// </summary>
public static class RoslynkActivitySource
{
	public const string Name = "Morris.Roslynk";

	public static readonly ActivitySource Instance = new(Name);
}
