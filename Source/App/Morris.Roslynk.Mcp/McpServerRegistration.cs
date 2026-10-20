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
			Roslynk provides semantic inspection, diagnostics, navigation, references, call graphs,
			dead-code analysis and edits for .cs/.razor/.cshtml files. Prefer it over grep, source reads,
			hand edits and dotnet build for semantic questions; use dotnet test for runtime behavior.

			## Scope

			Only solution-compiled files are visible. For missing files, use standard tools and re-check
			later for project inclusion. Use standard file tools for diffs and line-range reads, including
			locations from PR diffs, and for non-code files.

			## Lifecycle

			open_solution returns solutionId; it waits for the initial load by default (noWait=true pre-warms in
			the background). Every solution tool waits server-side for an in-flight load up to the configured
			timeout; only if that elapses does it return error=Indexing (with projects=loaded/?), and then you
			should retry rather than read or edit files directly.
			The watcher folds edits, including yours. Never initiate reload_solution; suggest it after
			branch switches, restore, SDK/build-property changes.

			## Output protocol

			Text results: key=value headers, blank line, body (outlines tab-indented). Without a blank line,
			all lines are headers. status appears only when not Ready; booleans are Y/N.
			Leaves: kind,name,loc[,extra]; multiple field values use pipes; comma-containing values use
			single quotes. Output paths are solution-relative; projects omit .csproj.
			Generated declarations use virtual paths marked generated=Y/generator=<type>; read via
			get_symbol_body, not disk.

			## Parameters

			Names are consistent across tools: solutionPath opens a solution, solutionId is the handle every
			other tool takes, symbolName identifies any symbol (a type, member or Member:parameter), and
			filePath identifies a source file. Paths accept absolute or solution-relative forms. When a
			filePath matches no file exactly but names exactly one loaded file by path suffix or file name,
			tools use it and add resolvedPath=<solution-relative path>; several matches return NotFound with
			candidates. filePath accepts .cs/.razor/.cshtml unless restricted by the tool. Source lines/columns
			are 1-based; selections start inclusive, end exclusive. checkOnly=true previews without writing,
			returning would-change paths (apply_patch: headers only). Omitted optional arguments use defaults.

			## Name grammar

			Use returned fully-qualified names verbatim (resolvedSymbol/fullName/candidates), never short
			outline leaves. Methods/indexers accept overload parameter types: Ns.T.M(int, string),
			Ns.T.this[int]; parameter names/defaults/nullability are ignored. Bare generic names resolve
			unique arities; multiple arities return Ambiguous; same-named non-generics win.
			A partially qualified name (a dotted-segment suffix, e.g. UIHelper.Grid) resolves when exactly
			one symbol's fully-qualified name ends with those whole segments; several suffix matches return
			Ambiguous. Whole segments only: UIHelper never matches SomeUIHelper. Exact and relaxed full-name
			matches always win over suffix matches. get_symbol/get_symbol_body echo resolvedSymbol for a
			suffix-resolved request.
			Local functions nest under declaring members: Ns.T.M.local.

			## Conditional coverage

			Loaded frameworks plus variants toggling one uniformly defined/undefined C# symbol.
			Branches requiring simultaneous toggles may be uncovered.

			## Failures and freshness

			Failures are header-only: error=<code>, errorMessage=..., optional candidates.
			An argument the tool does not declare returns error=UnknownArgument naming the valid parameters;
			nothing ran, so retry with the listed names. Stale: target changed since reading; recompute/retry.
			Re-query after relevant writes/user saves.

			## Paging

			Only SupportsPaging=Y tools page. startItem is 1-based; continue with startItem=<nextStartItem>,
			keeping other arguments unchanged. Pages repeat enclosing containers. maxSize limits page characters
			(0 uses the response budget). get_diagnostics instead counts maxSize listed entries (0 lists all) and
			find_dead_code caps its scan by it. No tool drops results: everything found is reachable by paging.
			partialItem=<n> means an oversized item is incomplete and its remainder unretrievable:
			narrow the query; never claim complete coverage. Unpaged oversized results stop at a whole line
			with outputTruncated=Y, fullOutputChars=<n>.

			## Reads and writes

			find_reads/find_writes target fields/properties or Member:parameter; Member names a method,
			constructor, indexer or local function, optionally with overload parameter types.
			Other target kinds return NotSupported.
			Parameter resolvedSymbol: Member:parameter. Leaves: <memberKind>,<memberName>,
			<loc>,<accessKind>. accessKind: read, assign (=), compound (+=, ??=, ...), increment
			(++/--), ref (ref arguments, ref x, &x), out, init (declarations, object/with initializers,
			attribute named arguments, constructor assignments to its own fields/properties).
			compound/increment/ref appear in both tools. Counts overlap; do not sum.
			nameof, documentation crefs and named call arguments are excluded.

			## Batching

			Batch two or more independent, already-nameable facts with multi_query; run dependent discovery
			first, then batch follow-ups. Its description covers operations/continuations.

			## Editing and diagnostics

			Preview broad edits; validation failures write nothing. Semantic write results: applied,
			tool-specific headers, blank line, changed paths; applied=N for previews.
			Individual replacements are atomic; saving failures trigger attempted rollback, which may fail.
			Semantic edits validate their original snapshot and return Stale for intervening target changes.
			apply_patch's baseVersions is an optional extra guard.

			Finish edits, then call get_diagnostics once (cached). Use apply_code_fix (id/line/column).
			On Conflict with candidates, pass the chosen actionId to apply_code_action.
			Do not hand-edit declarations solely to silence diagnostics unless asked.

			## Razor

			Positions refer to C# in Razor source (@code/@functions, @{ }, expressions).
			Edits map generated C# to source: unmappable edits return NotSupported; mismatched source,
			Conflict. Analyzers skip generated Razor; compiler/RZxxxx diagnostics and CS8019/IDE0005
			unused-using fixes are supported. _Imports.razor/_ViewImports.cshtml usings are never removed.
			""";

		options.NameTracesAfterTools();
	}
}
