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
	public const string SolutionId = "Solution handle returned by open_solution.";

	public const string StartItem =
		"1-based first item of the page; only pass it to continue from nextStartItem=.";

	public const string MaxSize = "Maximum page size in characters; 0 uses the response budget.";

	public const string CheckOnly =
		"If true, returns the files that would change without writing anything.";

	public const string DocumentPath =
		"Path of the .cs, .razor or .cshtml file; absolute, or relative to the solution folder.";
}
