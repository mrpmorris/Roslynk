namespace Morris.Roslynk.Infrastructure.Results;

/// <summary>
/// The category of a failed <see cref="ResultBase"/>, so a caller can branch on the kind of failure
/// without parsing the message.
/// </summary>
public enum ErrorCode
{
	/// <summary>The solution model is still loading and no snapshot is available yet. Retry shortly.</summary>
	Indexing,

	/// <summary>The solution failed to load; the message carries the build/load failure. Fix and reload.</summary>
	Faulted,

	/// <summary>Nothing matched the requested name, symbol, document, or solution handle.</summary>
	NotFound,

	/// <summary>The request matched several symbols; <see cref="Error.Candidates"/> lists them.</summary>
	Ambiguous,

	/// <summary>The target or operation is outside Roslynk's supported surface (e.g. a non-C# file).</summary>
	NotSupported,

	/// <summary>A file changed on disk since it was read; <see cref="Error.StaleFiles"/> lists them. Re-read and retry.</summary>
	Stale,

	/// <summary>The request itself was malformed — a bad identifier, an unparseable patch, an invalid span.</summary>
	Invalid,

	/// <summary>The change could not be applied because it conflicts with the current state.</summary>
	Conflict
}
