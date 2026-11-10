using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins.V1;

/// <summary>
/// The outcome of a UseSolutionAsync call. Only the host creates instances; the State and the
/// payload always agree — Success carries both Solution and Result, Faulted carries FaultMessage,
/// NotFound carries neither.
/// </summary>
/// <typeparam name="T">The callback's return type.</typeparam>
public interface IUseSolutionResult<T>
{
	/// <summary>Why the call ended. The payload properties always agree with it.</summary>
	UseSolutionResultState State { get; }

	/// <summary>The load failure reason, or the reason a callback could not run to a terminal outcome. Set only when the state is Faulted.</summary>
	string? FaultMessage { get; }

	/// <summary>True when the callback ran against a ready, current snapshot.</summary>
	bool IsSuccess { get; }

	/// <summary>
	/// The snapshot the callback ran against.
	/// Throws <see cref="InvalidOperationException"/> when the state is not Success.
	/// </summary>
	Solution Solution { get; }

	/// <summary>
	/// The callback's return value.
	/// Throws <see cref="InvalidOperationException"/> when the state is not Success.
	/// </summary>
	T Result { get; }
}
