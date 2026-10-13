namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>
/// Descriptions shared verbatim by identical parameters of several tools. A parameter whose meaning
/// differs from a shared constant keeps its own text (get_symbol_body's startItem counts lines of the
/// returned text, find_dead_code's maxResults caps a scan, remove_unused_usings' documentPath is
/// optional and names a single file); this class is only for contracts that are genuinely identical.
/// Defaults are not stated here: the wrapper moves each schema default into the published description.
/// </summary>
internal static class ParameterDescriptions
{
	public const string SolutionId = "Solution handle.";

	public const string StartItem =
		"First item (1-based).";

	public const string MaxSize = "Page character limit.";

	public const string CheckOnly =
		"Preview only.";

	public const string DocumentPath =
		"Source file path.";
}
