namespace Morris.Roslynk.Plugins.V1;

/// <summary>Why a <see cref="IUseSolutionResult{T}"/> call ended.</summary>
public enum UseSolutionResultState
{
	/// <summary>The callback ran against a ready, current snapshot.</summary>
	Success,

	/// <summary>No solution file exists at the requested path. The callback did not run.</summary>
	NotFound,

	/// <summary>The load failed. The callback did not run. <c>FaultMessage</c> carries the build/load failure.</summary>
	Faulted,
}
