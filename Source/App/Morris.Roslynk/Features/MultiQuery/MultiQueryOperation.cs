using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Morris.Roslynk.Features.MultiQuery;

/// <summary>
/// The tools a multi_query operation may invoke: exactly the read-only query tools that run purely on the
/// pinned solution snapshot. The enum is the contract, not a convenience - the published schema advertises
/// these names, so an operation naming anything else (a write tool, get_diagnostics, an unknown name) is
/// unrepresentable and is refused at argument binding before multi_query ever runs. Member names are spelled
/// exactly like each tool's 'public const string <Tool>Name'; the catalog resolution test keeps the two in
/// lockstep.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultiQueryOp
{
	get_symbol,
	get_symbol_body,
	get_members,
	find_definition,
	find_implementations,
	find_references,
	get_callers,
	get_callees,
	search_symbols,
	get_type_hierarchy,
	find_dead_code,
	find_dead_conditionals,
}

/// <summary>
/// One operation inside a multi_query batch: which tool to run, plus its arguments. The arguments use
/// exactly that tool's single-call parameter names - anything else is rejected rather than ignored, so a
/// misspelled parameter can never silently fall back to its default. 'solutionId' is deliberately not an
/// argument: the batch carries it once, and every operation runs against the snapshot pinned from it.
/// </summary>
public sealed record MultiQueryOperation(
	[Description("Which read-only tool to run; that tool's own schema documents its parameter names.")]
	MultiQueryOp Tool,
	[Description("The tool's parameters, named exactly as its single-call parameters.")]
	IReadOnlyDictionary<string, JsonElement>? Arguments = null);
