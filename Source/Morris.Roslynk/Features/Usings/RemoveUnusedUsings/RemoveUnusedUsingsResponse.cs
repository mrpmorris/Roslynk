namespace Morris.Roslynk.Features.Usings.RemoveUnusedUsings;

/// <summary>
/// The outcome of removing unnecessary using directives. On success <c>Applied</c> is true and
/// <c>ChangedFiles</c> lists what was rewritten; <c>RemovedCount</c> is the number of directives removed.
/// A <c>checkOnly</c> preview leaves <c>Applied</c> false with the would-be changed files. A
/// <c>Message</c> explains a no-op or refusal.
/// </summary>
public sealed record RemoveUnusedUsingsResponse(
	bool Applied,
	IReadOnlyList<string> ChangedFiles,
	int RemovedCount,
	string? Message);
