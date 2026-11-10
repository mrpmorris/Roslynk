using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Morris.Roslynk.Infrastructure.Lifecycle;

/// <summary>
/// The result of a deferred diagnostics build: the computed <paramref name="Diagnostics"/> together with the
/// exact <paramref name="Solution"/> snapshot they were compiled from, so the caller formats locations
/// against the same trees they were produced from (a write could otherwise swap the snapshot underneath).
/// <see cref="Razor"/> carries the snapshot's own Razor compiler diagnostics (RZxxxx), captured at
/// publication time — the generation that produced this snapshot — so a held snapshot never serves a newer
/// generation's Razor set, and nothing reads Razor state through a possibly-disposed workspace. It is a
/// plain diagnostic array: the Lifecycle layer never names Razor types.
/// </summary>
public sealed record DiagnosticsResult(
	IReadOnlyList<Diagnostic> Diagnostics,
	Solution Solution,
	ImmutableArray<Diagnostic> Razor);