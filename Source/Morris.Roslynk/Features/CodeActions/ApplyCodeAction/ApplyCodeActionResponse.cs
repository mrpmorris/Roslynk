namespace Morris.Roslynk.Features.CodeActions.ApplyCodeAction;

/// <summary>
/// The outcome of applying a code action (or fix). On success <c>Applied</c> is true and
/// <c>ChangedFiles</c> lists what was rewritten; a <c>checkOnly</c> preview leaves <c>Applied</c> false
/// with the would-be changed files. <c>Action</c> echoes which action ran; <c>Message</c> explains any
/// no-op or refusal.
/// </summary>
public sealed record ApplyCodeActionResponse(
	bool Applied,
	IReadOnlyList<string> ChangedFiles,
	string? Action,
	string? Message);
