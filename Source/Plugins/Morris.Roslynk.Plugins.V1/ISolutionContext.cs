using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins.V1;

/// <summary>The snapshot a UseSolutionAsync callback runs against.</summary>
public interface ISolutionContext
{
	/// <summary>The immutable snapshot the callback runs against.</summary>
	Solution Solution { get; }

	/// <summary>
	/// Compiler, analyzer and Razor diagnostics of every severity, including hidden, for
	/// <see cref="Solution"/>. The same cached computation the built-in get_diagnostics tool
	/// uses: for the same solution snapshot and analyzer mode, whichever caller arrives first
	/// starts the computation and the other awaits the exact same task. Plugins filter the
	/// returned diagnostics themselves.
	/// </summary>
	ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}
