using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.GetSymbolBody;

[McpServerToolType]
public sealed class GetSymbolBodyTool
{
	/// <summary>The tool name; pages long declarations with startItem/maxSize.</summary>
	public const string GetSymbolBodyName = "get_symbol_body";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public GetSymbolBodyTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Returns verbatim declarations of methods, constructors, properties, indexers, events, fields,
		local functions or types. Preserves indentation, line endings and spacing; Razor bodies are
		generated C#.

		## Result

		One declaration: project, path and loc headers, a blank line, then source text.
		Partial types/methods: parts=<n>, then blocks headed
		part=<n>,project=...,path=...,loc=...,lines=<count>. Includes generated implementations
		(e.g. GeneratedRegex, LoggerMessage, LibraryImport).

		## Errors

		Ambiguous names/overloads return candidates. Symbols without source (namespaces or metadata-only
		symbols) return NotSupported. Metadata spellings of source types (Ns.Box`1, N.Outer+Inner)
		return their source.

		## Paging exceptions

		startItem counts returned source lines, including leading trivia when requested; count is total
		lines. Keep includeLeadingTrivia and part unchanged. startItem past the end returns Invalid with
		count. It pages one part; using it with multiple parts without part returns Invalid.

		Parts exceeding the budget are listed without text as omitted=Y; headers include truncated=Y
		and omittedParts=<k>. Fetch omitted parts with part=<n>, then page normally.
		""")]
	[McpServerTool(
		Name = GetSymbolBodyName,
		Title = "Get symbol source text",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> GetSymbolBody(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Fully-qualified symbol name; reuse names returned by discovery. Include parameter types to select one overload.")] string symbolName,
		[Description("Include the declaration's leading comments, XML documentation and preceding directives.")] bool includeLeadingTrivia = false,
		[Description("1-based first line of the page, counted in the returned text (its first comment line with includeLeadingTrivia), not in the file. Only pass it to continue from nextStartItem=.")] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description("1-based part to return when the symbol has several declarations; 0 returns every part (omitting the oversized ones).")] int part = 0,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);
		return await GetSymbolBodyCoreAsync(model, instance, symbolName, includeLeadingTrivia, startItem, maxSize, part, Budget, cancellationToken);
	}

	internal async Task<string> GetSymbolBodyCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string symbolName,
		bool includeLeadingTrivia = false,
		int startItem = 1,
		int maxSize = 0,
		int part = 0,
		ResponseBudget? budget = null,
		CancellationToken cancellationToken = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve across every projection so a symbol declared only in a branch inactive in the loaded
		// configuration is still found; grouping is signature-aware, so overloads stay separate groups.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups =
			await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName, cancellationToken);

		if (groups.Count == 0)
		{
			// Nothing in source. A name that still resolves against a referenced assembly is a real symbol we
			// simply cannot show a body for, which is NotSupported rather than NotFound — unless the name is a
			// metadata spelling of something the solution declares in source, which does have a body.
			IReadOnlyList<ISymbol> metadata =
				await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, symbolName, cancellationToken);

			List<ISymbol> declared = metadata.Where(symbol => DeclarationReferences(symbol).Any()).ToList();
			if (declared.Count > 1)
				return Failure(SymbolAmbiguity.Ambiguous(symbolName, declared));
			if (declared.Count == 1)
				return await RenderBodyAsync([declared[0]], symbolName, model, model.Solution, solutionDirectory, Failure, includeLeadingTrivia, startItem, maxSize, part, budget, cancellationToken);

			if (metadata.Count > 0)
			{
				string assembly = metadata[0].ContainingAssembly is { } owner ? $" It comes from '{owner.Name}'." : "";
				return Failure(Error.NotSupported(
					$"'{symbolName}' has no source declaration, so it has no body to return.{assembly} Use get_symbol for its signature."));
			}

			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(model.Solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(symbolName, groups.Select(group => group[0].Symbol)));

		ProjectionSymbol resolved = groups[0][0];
		return await RenderBodyAsync([resolved.Symbol], symbolName, model, resolved.Projection.Solution, solutionDirectory, Failure, includeLeadingTrivia, startItem, maxSize, part, budget, cancellationToken);
	}

	/// <summary>
	/// Renders the verbatim source text of one logical symbol: ambiguous input is a failure, a symbol with
	/// no source declaration is NotSupported, and otherwise every declaration part is built, paged within
	/// maxSize and the response budget, and framed.
	/// </summary>
	private static async Task<string> RenderBodyAsync(
		IReadOnlyList<ISymbol> symbols,
		string requestedName,
		SolutionModel model,
		Solution solution,
		string? solutionDirectory,
		Func<Error, string> failure,
		bool includeLeadingTrivia,
		int startItem,
		int maxSize,
		int part,
		ResponseBudget? budget,
		CancellationToken cancellationToken)
	{
		if (symbols.Count > 1)
			return failure(SymbolAmbiguity.Ambiguous(requestedName, symbols));

		ISymbol symbol = symbols[0];
		if (!DeclarationReferences(symbol).Any())
		{
			string where = symbol.ContainingAssembly is { } assembly ? $" It comes from '{assembly.Name}'." : "";
			return failure(Error.NotSupported(
				$"'{requestedName}' has no source declaration, so it has no body to return.{where} Use get_symbol for its signature."));
		}

		var parts = new List<Part>();
		foreach (SyntaxReference reference in DeclarationReferences(symbol))
		{
			Part? candidate = await BuildPartAsync(reference, solution, solutionDirectory, includeLeadingTrivia, cancellationToken);
			if (candidate is not null)
				parts.Add(candidate);
		}

		if (parts.Count == 0)
			return failure(Error.NotSupported($"'{requestedName}' has no readable source declaration."));

		// Paging validation comes after resolution: a bad part on a nonexistent symbol is NotFound, the more
		// useful answer. From here on every rejection names the argument that caused it.
		if (maxSize < 0)
			return failure(Error.Invalid($"'maxSize' must be at least 0 (got {maxSize}). There is no unlimited mode: the response budget is the ceiling, so page a long declaration with startItem."));

		if (part < 0 || part > parts.Count)
			return failure(Error.Invalid(
				$"'{requestedName}' has {parts.Count} parts; part must be 0 to {parts.Count}."));

		if (startItem < 1)
			return failure(Error.Invalid(
				$"'startItem' counts the returned text's lines from 1 (got {startItem})."));

		if (startItem > 1 && parts.Count > 1 && part == 0)
			return failure(Error.Invalid(
				$"'{requestedName}' is declared in {parts.Count} parts; pass part=<n> to page one of them."));

		if (parts.Count == 1 || part > 0)
		{
			Part selected = parts.Count == 1 ? parts[0] : parts[part - 1];
			Action<OutlineBuilder> addHeaders = parts.Count == 1
				? builder => AddLocationHeaders(builder, selected)
				: builder =>
				{
					builder.Header("parts", parts.Count);
					builder.Header("part", part);
					AddLocationHeaders(builder, selected);
				};
			return PagedText(addHeaders, selected.Text, model.Status, startItem, maxSize, budget, failure);
		}

		int ceiling = (budget ?? ResponseBudget.Default).MaxChars;
		return SeveralParts(parts, model.Status, ceiling);
	}

	/// <summary>
	/// The syntax references of every declaration of the symbol. A partial method, property or event is two
	/// symbols in Roslyn - the defining declaration and the implementation - and name lookup returns only the
	/// definition, so the implementation part (hand-written, or supplied by a source generator) is followed
	/// explicitly. The definition comes first.
	/// </summary>
	private static IEnumerable<SyntaxReference> DeclarationReferences(ISymbol symbol)
	{
		(ISymbol definition, ISymbol? implementation) = symbol switch
		{
			IMethodSymbol method => (method.PartialDefinitionPart ?? method, method.PartialImplementationPart),
			IPropertySymbol property => (property.PartialDefinitionPart ?? property, property.PartialImplementationPart),
			IEventSymbol @event => (@event.PartialDefinitionPart ?? @event, @event.PartialImplementationPart),
			_ => (symbol, (ISymbol?)null)
		};

		IEnumerable<SyntaxReference> references = definition.DeclaringSyntaxReferences;
		if (implementation is not null)
			references = references.Concat(implementation.DeclaringSyntaxReferences);

		return references.Distinct();
	}

	private static void AddLocationHeaders(OutlineBuilder builder, Part part)
	{
		if (part.Project is not null)
			builder.Header("project", part.Project);
		builder.Header("path", part.Path);
		builder.Header("loc", part.Location);
		if (part.Generated)
		{
			builder.Header("generated", true);
			if (part.Generator is not null)
				builder.Header("generator", part.Generator);
		}
	}

	/// <summary>
	/// Renders one text - a single part's, or one selected part's - paged within maxSize and the budget.
	/// A fitting result is byte-identical to the unpaged output: the paging headers exist only when lines
	/// were left out ("absent when nothing was dropped"), and they are headers, so they precede the blank
	/// line. Items are the text's lines, so the shared pager's chain machinery degenerates to plain line
	/// paging with partialItem standing in for partialLine.
	/// </summary>
	private static string PagedText(Action<OutlineBuilder> addHeaders, string text, SolutionStatus status, int startItem, int maxSize, ResponseBudget? budget, Func<Error, string> failure)
	{
		PagedRender render = ItemPaging.Render(
			status,
			addHeaders,
			builder => builder.Line(0, text),
			LineItems(text),
			startItem,
			maxSize,
			budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}

	/// <summary>The text's lines as paged items, one per LF with the CR kept on its own line; an
	/// unterminated final line counts, an empty text has none.</summary>
	private static IReadOnlyList<OutlineItem> LineItems(string text)
	{
		var items = new List<OutlineItem>();
		int start = 0;
		while (true)
		{
			int newline = text.IndexOf('\n', start);
			if (newline < 0)
			{
				if (start < text.Length)
					items.Add(OutlineItem.Single(text[start..], []));
				break;
			}

			items.Add(OutlineItem.Single(text[start..newline], []));
			start = newline + 1;
		}

		return items;
	}

	/// <summary>
	/// Every part of a multi-part symbol. When the whole listing fits the ceiling it is the unpaged dump
	/// (each part line gaining its lines= count); otherwise it degrades to the whole-or-omitted overview:
	/// parts shown whole, greedily in declaration order, the rest listed with lines= and omitted=Y.
	/// </summary>
	private static string SeveralParts(IReadOnlyList<Part> parts, SolutionStatus status, int ceiling)
	{
		int[] lines = new int[parts.Count];
		long dumpChars = ("parts=" + parts.Count + "\n").Length
			+ (status == SolutionStatus.Ready ? 0 : ("status=" + status + "\n").Length)
			+ 1; // the blank line between headers and body
		for (int index = 0; index < parts.Count; index++)
		{
			lines[index] = LineWindow.CountLines(parts[index].Text);
			dumpChars += PartLine(index + 1, parts[index], lines[index], omitted: false).Length + 1
				+ parts[index].Text.Length + 1
				+ (index > 0 ? 1 : 0);
		}

		if (dumpChars <= ceiling)
			return AllParts(parts, status, lines);

		return PartsOverview(parts, status, lines, ceiling);
	}

	/// <summary>The complete listing: today's multi-part output with each part line carrying its lines= count.</summary>
	private static string AllParts(IReadOnlyList<Part> parts, SolutionStatus status, int[] lines)
	{
		var builder = new OutlineBuilder();
		builder.Header("parts", parts.Count);
		builder.Status(status);
		builder.BeginBody();

		for (int index = 0; index < parts.Count; index++)
		{
			if (index > 0)
				builder.Line(0, "");
			builder.Line(0, PartLine(index + 1, parts[index], lines[index], omitted: false));
			builder.Line(0, parts[index].Text);
		}

		return builder.ToString();
	}

	/// <summary>
	/// The whole-or-omitted overview. A part is shown whole or not at all - never cut - so a listing is never
	/// mistaken for a complete text. Greedy (not fair-share) is deliberate: the typical case is a small
	/// hand-written part beside a large generated one, and greedy hands back the part the caller wrote.
	/// </summary>
	private static string PartsOverview(IReadOnlyList<Part> parts, SolutionStatus status, int[] lines, int ceiling)
	{
		// The header block is reserved at its widest (omittedParts=<parts.Count>), so the greedy pass cannot
		// spend characters the real headers then take back.
		int headerChars = ("parts=" + parts.Count + "\n").Length
			+ "truncated=Y\n".Length
			+ ("omittedParts=" + parts.Count + "\n").Length
			+ (status == SolutionStatus.Ready ? 0 : ("status=" + status + "\n").Length)
			+ 1; // the blank line between headers and body

		var shown = new bool[parts.Count];
		int remainingChars = ceiling - headerChars;
		int omittedCount = 0;
		for (int index = 0; index < parts.Count; index++)
		{
			int lineChars = PartLine(index + 1, parts[index], lines[index], omitted: true).Length + 1;
			int textChars = parts[index].Text.Length + 1;
			int separatorChars = index > 0 ? 1 : 0;
			bool fits = lineChars + textChars + separatorChars <= remainingChars;
			shown[index] = fits;
			remainingChars -= lineChars + separatorChars;
			if (fits)
			{
				remainingChars -= textChars;
			}
			else
			{
				omittedCount++;
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("parts", parts.Count);
		if (omittedCount > 0)
		{
			builder.Header("truncated", true);
			builder.Header("omittedParts", omittedCount);
		}
		builder.Status(status);
		builder.BeginBody();

		for (int index = 0; index < parts.Count; index++)
		{
			if (index > 0)
				builder.Line(0, "");
			builder.Line(0, PartLine(index + 1, parts[index], lines[index], omitted: !shown[index]));
			if (shown[index])
				builder.Line(0, parts[index].Text);
		}

		return builder.ToString();
	}

	/// <summary>
	/// A part line: positional fields with lines= (and, for an omitted part, omitted=Y) appended at the end,
	/// so the fields every existing parser knows keep their positions.
	/// </summary>
	private static string PartLine(int number, Part part, int lines, bool omitted)
	{
		string project = part.Project is null ? "" : $"project={OutlineBuilder.Field(part.Project)},";
		string generated = part.Generated
			? ",generated=Y" + (part.Generator is null ? "" : $",generator={OutlineBuilder.Field(part.Generator)}")
			: "";
		return $"part={number},{project}path={part.Path},loc={part.Location}{generated},lines={lines}{(omitted ? ",omitted=Y" : "")}";
	}

	private static async Task<Part?> BuildPartAsync(
		SyntaxReference reference,
		Solution solution,
		string? solutionDirectory,
		bool includeLeadingTrivia,
		CancellationToken cancellationToken)
	{
		SyntaxNode node = await reference.GetSyntaxAsync(cancellationToken);

		// A field/event symbol declares a VariableDeclaratorSyntax; the declaration a caller wants to read is
		// the whole field declaration (modifiers, type and initializer) that owns it.
		if (node is VariableDeclaratorSyntax variable && variable.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>() is { } field)
			node = field;

		TextSpan span = includeLeadingTrivia ? WithLeadingTrivia(node) : node.Span;
		SourceText text = await reference.SyntaxTree.GetTextAsync(cancellationToken);
		FileLinePositionSpan display = reference.SyntaxTree.GetDisplaySpan(span);

		string? path = SolutionRelativePath.Of(solutionDirectory, display.Path);
		if (path is null)
			return null;

		return new Part(
			ProjectName.Of(solution, reference.SyntaxTree),
			path,
			$"{display.StartLinePosition.Line + 1}:{display.StartLinePosition.Character + 1}-{display.EndLinePosition.Line + 1}:{display.EndLinePosition.Character + 1}",
			text.ToString(span),
			GeneratedSource.IsGenerated(solution, reference.SyntaxTree),
			GeneratedSource.GeneratorOf(solution, reference.SyntaxTree));
	}

	/// <summary>
	/// The node's span extended back over its leading comments, XML documentation and directives. The blank
	/// lines and indentation that separate the declaration from whatever precedes it are excluded, so the
	/// result starts at the first line that actually belongs to the declaration.
	/// </summary>
	private static TextSpan WithLeadingTrivia(SyntaxNode node)
	{
		SyntaxTriviaList trivia = node.GetLeadingTrivia();
		foreach (SyntaxTrivia candidate in trivia)
		{
			if (candidate.IsKind(SyntaxKind.WhitespaceTrivia)
				|| candidate.IsKind(SyntaxKind.EndOfLineTrivia))
			{
				continue;
			}

			return TextSpan.FromBounds(candidate.FullSpan.Start, node.Span.End);
		}

		return node.Span;
	}

	private sealed record Part(string? Project, string Path, string Location, string Text, bool Generated, string? Generator);
}
