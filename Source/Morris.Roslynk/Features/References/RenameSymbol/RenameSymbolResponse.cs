namespace Morris.Roslynk.Features.References.RenameSymbol;

/// <summary>
/// The outcome of a rename. On success <c>Applied</c> is true and <c>ChangedFiles</c> lists what was
/// rewritten. When the name is ambiguous, <c>Candidates</c> lists the matches; on any refusal a
/// <c>Message</c> explains why and nothing was written.
/// </summary>
public sealed record RenameSymbolResponse(
	bool Applied,
	string? ResolvedSymbol,
	IReadOnlyList<string> ChangedFiles,
	IReadOnlyList<string> Candidates,
	string? Message)
{
	public static RenameSymbolResponse Failed(string message) =>
		new(Applied: false, ResolvedSymbol: null, ChangedFiles: [], Candidates: [], Message: message);

	public static RenameSymbolResponse Ambiguous(IReadOnlyList<string> candidates) =>
		new(Applied: false, ResolvedSymbol: null, ChangedFiles: [], Candidates: candidates,
			Message: "The name is ambiguous; rename using one of the candidate fully-qualified names.");
}
