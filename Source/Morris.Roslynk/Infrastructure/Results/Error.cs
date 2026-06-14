namespace Morris.Roslynk.Infrastructure.Results;

/// <summary>
/// A structured failure carried by <see cref="ResultBase.Error"/>. A <see cref="Code"/> and human
/// <see cref="Message"/> are always present; <see cref="Candidates"/> and <see cref="StaleFiles"/> carry
/// the only payloads a caller acts on, for the ambiguous/not-found and stale cases respectively.
/// </summary>
public sealed class Error
{
	public required ErrorCode Code { get; init; }
	public required string Message { get; init; }
	public IReadOnlyList<string>? Candidates { get; init; }
	public IReadOnlyList<string>? StaleFiles { get; init; }

	public static Error Indexing(string message = "The solution is still loading; retry shortly.") =>
		new() { Code = ErrorCode.Indexing, Message = message };

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

	public static Error Conflict(string message) =>
		new() { Code = ErrorCode.Conflict, Message = message };
}
