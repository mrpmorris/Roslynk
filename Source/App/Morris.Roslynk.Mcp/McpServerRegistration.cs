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
			Version = "1.0.0"
		};

		// Shared guidance has one home here; tool attributes carry only their own contracts.
		options.ServerInstructions =
			"""
			Roslynk provides semantic inspection, diagnostics, navigation, references, call graphs,
			dead-code analysis and edits for compiled C#, Razor and CSHTML. Prefer these tools over
			grep, source reads, hand edits and dotnet build for semantic questions. Use dotnet test
			when runtime behavior needs validation. Inspect the relevant tool description and schema
			before use; omit parameters whose defaults match your intent.

			## Scope

			Roslynk sees solution-compiled files. If a file is not found, use standard file tools; re-check
			on later use in case it joined a project. Standard tools may inspect diffs, non-code files
			(.csproj, .json, .md) and bounded source excerpts at Roslynk-reported locations.

			## Lifecycle

			open_solution returns solutionId for subsequent calls. During loading, error=Indexing means retry shortly or poll
			get_solution_status. The watcher folds edits, including your own. Never initiate reload_solution;
			suggest it when the model may be stale after a branch switch, restore, SDK or build-property change.

			## Output protocol

			Results are text: key=value headers, a blank line, then a tab-indented body. Without a blank line,
			all lines are headers. status appears only when not Ready. Newlines are '\n'; booleans are Y/N.
			Omit parameters whose defaults match your intent.

			Leaves use kind,name,loc[,extra]. Kinds: method|localfunction|property|field|event|class|struct|
			interface|enum|delegate. loc is line:col or startLine:startCol-endLine:endCol; multiple locations
			use pipes. Comma-containing values are single-quoted; extra fields follow the tool's documented order.

			Paths are solution-relative. Source results identify the project by project-file name without
			.csproj (other extensions retained): #project beside #path for a single source, or the outermost
			node in nested results. Metadata results omit the project. Nested paths split folder and filename
			across successive levels. Generator declarations use virtual paths with generated=Y and
			generator=<type>; read them with get_symbol_body rather than from disk.

			## Name grammar

			Reuse returned fully-qualified names verbatim, including candidate names from Ambiguous/NotFound.
			Methods/indexers accept parameter types to select overloads: Ns.T.M(int, string), Ns.T.this[int].
			Parameter names, defaults and nullability are ignored; qualified parameter types are accepted.

			Bare generic names resolve when one arity exists; multiple arities return Ambiguous, and a
			same-named non-generic wins. Equivalent arity spellings: Box<T>, Box<int>, Box`1, Box<>, and
			metadata Ns.Outer`1+Inner. Local functions nest under their declaring member:
			Ns.T.M.local, Ns.T.M.outer.inner. Any segment may carry parameter types.

			## Workflow

			Discover exact symbol names before asking about relationships; then inspect references/callers
			and the relevant member bodies. To understand a method, start with its body: prefer member
			bodies over reading whole containing types.

			## Conditional coverage

			Covers loaded target frameworks plus variants toggling one symbol uniformly defined or undefined
			across C# projects. Branches needing multiple simultaneous toggles or mixed-definition symbols
			may be uncovered.

			## Failures and freshness

			Failures are header-only: error=<code>, errorMessage=..., optional candidate lines.
			Stale means a target changed since reading: recompute and retry. After relevant writes or user
			saves, re-query before relying on earlier snapshot results.

			## Paging

			Narrow queries to the task. Fetch pages as needed; retrieve all relevant pages before claiming
			complete coverage. A tool supports paging only if its description states SupportsPaging=Y.

			Paged results report count=<set size>, truncated=Y and nextStartItem=<k>. Continue with
			startItem=<k> and unchanged other arguments. Pages repeat enclosing containers and stand alone.
			maxSize bounds page characters (0 uses the response budget, default 80,000). maxResults caps the
			result set; count describes that capped set. Tool descriptions document exceptions.
			An item exceeding a page is partially returned with partialItem=<n>; the remainder is unretrievable.
			Unpaged oversized results stop after a whole line: outputTruncated=Y, fullOutputChars=<n>.
			Narrow the request and retry.

			## Batching

			Whenever you need two or more independent facts whose names or arguments are already known,
			send them as one multi_query batch; do dependent discovery first, then batch the follow-ups.
			Do not re-query results you already hold. See the multi_query tool description for supported
			operations and continuation.

			## Editing and diagnostics

			Preview broad edits with checkOnly=true. Validation failures write nothing. Individual file
			replacements are atomic; saving failures trigger attempted rollback, which may fail.
			Writes are stale-guarded.

			Complete the task's edits, then call get_diagnostics once; it compiles/analyzes affected projects
			and caches unchanged results. Fix diagnostics with apply_code_fix using id, line and column.
			On Conflict with candidates, pass the chosen actionId to apply_code_action.
			Do not hand-edit, patch or delete declarations solely to silence diagnostics unless the user asks.

			## Razor

			documentPath accepts .razor/.cshtml; positions refer to C# in the Razor source
			(@code/@functions, @{ }, expressions). Edits map generated C# back to source with its indentation.
			Unmappable edits return NotSupported; mismatched source returns Conflict.

			Analyzers skip Razor-generated code. Compiler diagnostics, RZxxxx and unused-using fixes
			(CS8019/IDE0005) are supported. Import-file usings (_Imports.razor/_ViewImports.cshtml) are never removed.
			""";

		options.NameTracesAfterTools();
	}
}
