namespace Morris.Roslynk.Plugins.Abstractions;

/// <summary>Why a <see cref="IUseSolutionResult{T}"/> call ended.</summary>
public enum UseSolutionResultState
{
	/// <summary>The callback ran against a ready, current snapshot.</summary>
	Success,

	/// <summary>No solution file exists at the requested path. The callback did not run.</summary>
	NotFound,

	/// <summary>The call ended without a snapshot matching the solution's current state. FaultMessage carries the reason: a load
	/// failure (the callback did not run), repeated supersession until the deadline expired,
	/// eviction during the callback (the callback may have run), or the solution already unloaded
	/// by shutdown (the callback did not run; no reload is attempted).</summary>
	Faulted,
}
