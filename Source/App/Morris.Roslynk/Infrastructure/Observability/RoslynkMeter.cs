using System.Diagnostics.Metrics;

namespace Morris.Roslynk.Infrastructure.Observability;

/// <summary>
/// The single <see cref="Meter"/> all Roslynk metrics are emitted from.
/// </summary>
public static class RoslynkMeter
{
	public const string Name = "Morris.Roslynk";

	public static readonly Meter Instance = new(Name);
}
