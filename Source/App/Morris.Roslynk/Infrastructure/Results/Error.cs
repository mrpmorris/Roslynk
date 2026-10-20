namespace Morris.Roslynk.Infrastructure.Results;

/// <summary>
/// A structured failure returned by a tool. A <see cref="Code"/> and human <see cref="Message"/> are always
/// present; <see cref="Candidates"/> and <see cref="StaleFiles"/> carry the only payloads a caller acts on,
/// for the ambiguous/not-found and stale cases respectively.
/// </summary>
public sealed class Error
{
	public required ErrorCode Code { get; init; }
	public required string Message { get; init; }
	public IReadOnlyList<string>? Candidates { get; init; }
	public IReadOnlyList<string>? StaleFiles { get; init; }

	/// <summary>
	/// The solution's initial load is still in flight after the bounded server-side wait. The message carries
	/// the live project count so the caller can see progress, e.g. <c>projects=9/?</c>.
	/// </summary>
	public static Error Indexing(int loadedProjects = 0, int? totalProjects = null) =>
		new()
		{
			Code = ErrorCode.Indexing,
			Message = $"The solution is still loading; retry this same call shortly (projects={loadedProjects}/{totalProjects?.ToString() ?? "?"}). Do not fall back to reading or editing the .cs files directly; loading finishes within seconds to a minute, after which this tool returns the compiler-backed result."
		};

	public static Error Faulted(string message) =>
		new() { Code = ErrorCode.Faulted, Message = message };

	public static Error NotFound(string message, IReadOnlyList<string>? candidates = null) =>
		new() { Code = ErrorCode.NotFound, Message = message, Candidates = candidates };

	public static Error Ambiguous(string message, IReadOnlyList<string> candidates) =>
		new() { Code = ErrorCode.Ambiguous, Message = message, Candidates = candidates };

	public static Error NotSupported(string message) =>
		new() { Code = ErrorCode.NotSupported, Message = message };

	public static Error Stale(string message, IReadOnlyList<string> staleFiles) =>
		new() { Code = ErrorCode.Stale, Message = message, StaleFiles = staleFiles };

	public static Error Invalid(string message) =>
		new() { Code = ErrorCode.Invalid, Message = message };

	public static Error UnknownArgument(string message) =>
		new() { Code = ErrorCode.UnknownArgument, Message = message };

	public static Error Conflict(string message, IReadOnlyList<string>? candidates = null) =>
		new() { Code = ErrorCode.Conflict, Message = message, Candidates = candidates };

	public static Error Truncated(string message) =>
		new() { Code = ErrorCode.Truncated, Message = message };

	public static Error Stale(string message) =>
		new() { Code = ErrorCode.Stale, Message = message, StaleFiles = [] };
}
