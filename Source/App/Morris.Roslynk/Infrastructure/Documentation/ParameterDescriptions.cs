namespace Morris.Roslynk.Infrastructure.Documentation;

/// <summary>
/// Descriptions shared verbatim by identical parameters of several tools. A parameter whose meaning
/// differs from a shared constant keeps its own text (get_symbol_body's startItem counts lines of the
/// returned text, find_dead_code's maxSize caps a scan, remove_unused_usings' filePath is optional
/// and names a single file); this class is only for contracts that are genuinely identical.
/// Defaults are not stated here: the wrapper moves each schema default into the published description.
/// </summary>
internal static class ParameterDescriptions
{
	public const string SolutionId = "Solution handle: the solutionId echoed by open_solution, or an absolute path; either slash form is accepted.";

	public const string SolutionPath = "Absolute path to the .sln or .slnx file.";

	public const string SymbolName = "Symbol name: fully qualified, or a dotted-segment suffix that uniquely matches one symbol.";

	public const string SymbolNameFilter = "Case-insensitive symbol-name filter: a bare name is a substring; a dotted name is container-qualified (last segment names the symbol, preceding segments a suffix of its containing type/namespace chain).";

	public const string FilePath = "Source file path.";

	public const string StartItem =
		"First item (1-based).";

	public const string MaxSize = "Page character limit.";

	public const string CheckOnly =
		"Preview only.";
}
