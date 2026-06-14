namespace Morris.Roslynk.Infrastructure.Common;

/// <summary>
/// The resolution outcome of a tool call: whether the target was answered, is genuinely
/// absent, or falls outside Roslynk's C#/solution scope.
/// </summary>
public enum ResultStatus
{
	Ok,
	NotFound,
	NotSupported
}
