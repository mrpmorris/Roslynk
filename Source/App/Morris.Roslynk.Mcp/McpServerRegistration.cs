using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Mcp.Observability;

namespace Morris.Roslynk.Mcp;

/// <summary>
/// Server identity and shared library guidance returned to clients during MCP initialization.
/// </summary>
internal static class McpServerRegistration
{
	public static void Configure(McpServerOptions options)
	{
		options.ServerInfo = new Implementation
		{
			Name = "Roslynk",
			Title = "Roslynk; C# semantic intelligence",
			Version = typeof(McpServerRegistration).Assembly.GetName().Version?.ToString() ?? "1.0.0"
		};

		// Shared guidance has one home here; tool attributes carry only their own contracts.
		options.ServerInstructions =
			"""
			Roslynk provides semantic inspection, diagnostics, navigation, references, call graphs, dead-code
			analysis and edits for compiled C#, Razor and CSHTML. Prefer it over grep, source reads, hand
			edits and dotnet build for semantic questions; use dotnet test for runtime behavior.

			## Scope

			Sees solution-compiled files only. If a file is not found, use standard file tools and re-check
			later (it may have joined a project); standard tools may inspect diffs, non-code files and
			bounded excerpts at Roslynk-reported locations.

			## Lifecycle

			open_solution returns solutionId for later calls; while loading, error=Indexing means retry
			shortly or poll get_solution_status. The watcher folds edits, including your own. Never initiate
			reload_solution; suggest it after a branch switch, restore, SDK or build-property change.

			## Output protocol

			Results are text: key=value headers, a blank line, then a tab-indented body; without that blank
			line all lines are headers. status appears only when not Ready; booleans are Y/N. Leaves are
			kind,name,loc[,extra]: pipe-delimited for multiple values in one field, single-quoted when the
			value contains a comma. Paths are solution-relative (inputs accept absolute); source results
			name the project beside path, without .csproj. Generated declarations sit at virtual paths
			marked generated=Y/generator=<type>: read them with get_symbol_body, not from disk.

			## Name grammar

			Reuse returned fully-qualified names verbatim (resolvedSymbol, fullName, candidates) - never a
			short outline leaf. Methods/indexers accept parameter types to select one overload:
			Ns.T.M(int, string), Ns.T.this[int]; parameter names, defaults and nullability are ignored. A
			bare generic name resolves when one arity exists; several arities return Ambiguous, and a
			same-named non-generic wins. Local functions nest under their declaring member: Ns.T.M.local.

			## Conditional coverage

			Loaded target frameworks, plus variants toggling one symbol uniformly defined or undefined
			across C# projects; branches needing several simultaneous toggles may be uncovered.

			## Failures and freshness

			Failures are header-only: error=<code>, errorMessage=..., optional candidate lines. Stale means
			a target changed since it was read: recompute and retry. After relevant writes or user saves,
			re-query before relying on earlier snapshot results.

			## Paging

			A tool pages only if its description states SupportsPaging=Y. nextStartItem=<k> is the only
			continuation indicator: resend startItem=<k> with unchanged other arguments; pages repeat
			enclosing containers and stand alone. maxResults caps the selected result set and count
			describes that capped set - paging cannot retrieve what a cap excluded. An item larger than a
			page is returned partially as partialItem=<n>, its remainder unretrievable: narrow the query
			and never claim complete coverage. Unpaged oversized results stop after a whole line:
			outputTruncated=Y, fullOutputChars=<n>. find_dead_code's maxResults caps the scan itself; its
			description explains.

			## Batching

			When two or more independent facts are already nameable, send one multi_query batch; run
			dependent discovery first, then batch the follow-ups. See its description for operations and
			continuation.

			## Editing and diagnostics

			Preview broad edits with checkOnly=true; validation failures write nothing. Individual file
			replacements are atomic; saving failures trigger attempted rollback, which may fail. Semantic
			edits validate against the snapshot they were computed from and report Stale rather than
			overwriting intervening changes; apply_patch's baseVersions is an optional extra guard.

			Complete the task's edits, then call get_diagnostics once (it caches); fix diagnostics with
			apply_code_fix (id, line, column). On Conflict with candidates, pass the chosen actionId to
			apply_code_action. Do not hand-edit declarations solely to silence diagnostics unless the user
			asks.

			## Razor

			documentPath accepts .razor/.cshtml; positions refer to C# in the Razor source (@code/@functions,
			@{ }, expressions). Edits map generated C# back to source; unmappable edits return NotSupported,
			mismatched source Conflict. Analyzers skip Razor-generated code; compiler diagnostics, RZxxxx
			and unused-using fixes (CS8019/IDE0005) are supported. Usings in
			_Imports.razor/_ViewImports.cshtml are never removed.
			""";

		options.NameTracesAfterTools();
	}
}
