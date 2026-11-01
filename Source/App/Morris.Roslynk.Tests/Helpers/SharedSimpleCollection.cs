namespace Morris.Roslynk.Tests;

using Xunit;

/// <summary>
/// Runs its member classes one at a time instead of concurrently. Those classes all query the same shared
/// Simple instance, so serialising them removes the concurrent join-the-load wait at the cost of the
/// parallelism they would otherwise have.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedSimpleCollection
{
	public const string Name = "SharedSimple";
}
