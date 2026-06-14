namespace Morris.Roslynk.Features.Signatures.ChangeSignature;

/// <summary>
/// The outcome of a change-signature. On success <c>Applied</c> is true, <c>ChangedFiles</c> lists what
/// was rewritten and <c>UpdatedCallSites</c> how many invocations gained the new argument. On any refusal
/// (<c>Applied</c> false) a <c>Message</c> explains why and nothing was written.
/// </summary>
public sealed record ChangeSignatureResponse(
	bool Applied,
	string? ResolvedMethod,
	IReadOnlyList<string> ChangedFiles,
	int UpdatedCallSites,
	string? Message)
{
	public static ChangeSignatureResponse Failed(string message) =>
		new(Applied: false, ResolvedMethod: null, ChangedFiles: [], UpdatedCallSites: 0, message);

	public static ChangeSignatureResponse NotSupported(string resolvedMethod, string message) =>
		new(Applied: false, resolvedMethod, ChangedFiles: [], UpdatedCallSites: 0, message);
}
