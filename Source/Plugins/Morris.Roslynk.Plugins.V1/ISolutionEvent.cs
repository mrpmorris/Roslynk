using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins.V1;

/// <summary>A solution lifecycle change pushed to a plugin as it happens.</summary>
public interface ISolutionEvent
{
	/// <summary>What changed.</summary>
	SolutionEventKind Kind { get; }

	/// <summary>
	/// The absolute solution path with forward slashes — the same handle built-in tools echo,
	/// and the value a plugin passes to <see cref="IPluginContext.UseSolutionAsync{T}"/>.
	/// </summary>
	string SolutionId { get; }

	/// <summary>
	/// The immutable snapshot the event describes, when one exists: null on Building (until the
	/// first load completes; the previous snapshot during a rebuild), on Failed, and on Evicted.
	/// </summary>
	Solution? Solution { get; }

	/// <summary>The failure reason, set only when <see cref="Kind"/> is <see cref="SolutionEventKind.Failed"/>.</summary>
	string? ErrorMessage { get; }
}
