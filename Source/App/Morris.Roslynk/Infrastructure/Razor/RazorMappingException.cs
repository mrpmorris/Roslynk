namespace Morris.Roslynk.Infrastructure.Razor;

/// <summary>How mapping a generated-document change back to its razor source failed.</summary>
public enum RazorMappingFailure
{
	/// <summary>The change lands in generated code with no #line mapping back to a .razor/.cshtml file.</summary>
	Unmappable,

	/// <summary>The mapped razor file is not loaded in the workspace as an additional document.</summary>
	MissingSource,

	/// <summary>The razor file's text at the mapped location does not match the generated document's.</summary>
	TextMismatch,
}

/// <summary>
/// Raised by <see cref="RazorChangeMapper"/> when a text change computed against a Razor-generated
/// document cannot be mapped back to (and verified against) its source .razor/.cshtml file. Callers
/// abort the whole edit rather than applying it partially.
/// </summary>
public sealed class RazorMappingException : Exception
{
	public RazorMappingException(RazorMappingFailure kind, string generatedFilePath, string message)
		: base(message)
	{
		Kind = kind;
		GeneratedFilePath = generatedFilePath;
	}

	public RazorMappingFailure Kind { get; }

	public string GeneratedFilePath { get; }
}
