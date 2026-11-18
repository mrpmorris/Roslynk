using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Plugins.Abstractions;

/// <summary>The snapshot a UseSolutionAsync callback runs against.</summary>
public interface ISolutionContext
{
	/// <summary>The immutable snapshot the callback runs against.</summary>
	Solution Solution { get; }

	/// <summary>
	/// Compiler, analyzer and Razor diagnostics of every severity, including hidden, for
	/// <see cref="Solution"/>. The same computation the built-in get_diagnostics tool uses: it is
	/// computed once per snapshot and always includes analyzers, so whichever caller arrives first
	/// starts it and the other awaits the exact same result. Plugins filter the returned
	/// diagnostics themselves.
	/// </summary>
	ValueTask<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}
