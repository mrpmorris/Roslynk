using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.References.RenameSymbol;

[McpServerToolType]
public sealed class RenameSymbolTool
{
	public const string RenameSymbolName = "rename_symbol";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ApplyPipeline ApplyPipeline;

	public RenameSymbolTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[McpServerTool(
		Name = RenameSymbolName,
		Title = "Rename a symbol",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		$"""
		Renames a symbol and all its references across the solution using Roslyn (correct across partial
		classes and code-behind; string literals and comments are left untouched). Symbols declared or
		referenced in .razor/.cshtml files are renamed by rewriting those files directly — edits computed
		against the Razor-generated code are mapped back through the compiler's #line directives, covering
		@code blocks, markup expressions, and component-attribute usages in other components' markup.
		Returns a text result, not
		JSON: 'applied', 'resolvedSymbol', 'status' header, a blank line, then one
		solution-relative changed-file path per line. {OutlineDescriptions.Project} {OutlineDescriptions.Freshness} Refuses an invalid identifier and reports candidates when
		the name is ambiguous. Pass checkOnly to preview the files that would change without writing. Prefer
		this over a textual find/replace rename; it renames the symbol itself, so it never touches unrelated
		same-named text.
		""")]
	public async Task<string> RenameSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol to rename.")] string symbolName,
		[Description("The new name (must be a valid C# identifier).")] string newName,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (!SyntaxFacts.IsValidIdentifier(newName))
			return Failure(Error.Invalid($"'{newName}' is not a valid C# identifier."));

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution baseSolution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(baseSolution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(baseSolution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName);
		if (groups.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(baseSolution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}
		if (groups.Count > 1)
		{
			string[] candidates = groups.Select(group => SymbolResolver.FullyQualifiedName(group[0].Symbol)).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous("The name is ambiguous; rename using one of the candidate fully-qualified names.", candidates));
		}

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		string resolvedName = SymbolResolver.FullyQualifiedName(resolved[0].Symbol);

		// Rename in each projection — each rewrites only its own active branch, semantically — then union the
		// per-file text changes (deduped by span), so a symbol used in both #if and #else branches, or across
		// target frameworks, is rewritten everywhere rather than leaving the inactive branch stale.
		var changesByPath = new Dictionary<string, Dictionary<TextSpan, TextChange>>(StringComparer.OrdinalIgnoreCase);
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			Solution projectionSolution = projectionSymbol.Projection.Solution;
			Solution renamed = await Renamer.RenameSymbolAsync(projectionSolution, projectionSymbol.Symbol, new SymbolRenameOptions(), newName);

			foreach (ProjectChanges projectChanges in renamed.GetChanges(projectionSolution).GetProjectChanges())
			{
				foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
				{
					Document originalDocument = projectionSolution.GetDocument(documentId)!;
					if (originalDocument.FilePath is not string path)
						continue;

					Document renamedDocument = renamed.GetDocument(documentId)!;
					if (!changesByPath.TryGetValue(path, out Dictionary<TextSpan, TextChange>? perSpan))
					{
						perSpan = [];
						changesByPath[path] = perSpan;
					}

					foreach (TextChange change in await renamedDocument.GetTextChangesAsync(originalDocument))
						perSpan[change.Span] = change;
				}
			}
		}

		// Changes that landed in Razor-generated .g.cs documents can never be persisted; map each one back
		// to the .razor/.cshtml source it came from (via the compiler's #line mapping) so that file is
		// edited instead. Mapping failures abort here, before anything is applied or written.
		Dictionary<string, Dictionary<TextSpan, TextChange>> razorChangesByPath;
		try
		{
			razorChangesByPath = await MapRazorGeneratedChangesAsync(baseSolution, changesByPath);
		}
		catch (RazorMappingException exception)
		{
			return Failure(exception.Kind == RazorMappingFailure.TextMismatch
				? Error.Conflict(exception.Message)
				: Error.NotSupported(exception.Message));
		}

		// Apply the unioned changes onto the base solution (every document that shares the file), so the write
		// path persists each file once and the published snapshot reflects all branches. Razor-generated
		// documents keep their in-memory rename here too, so the published snapshot stays consistent with the
		// rewritten .razor sources without re-running the generator.
		Solution updated = baseSolution;
		foreach ((string path, Dictionary<TextSpan, TextChange> perSpan) in changesByPath)
		{
			List<TextChange> ordered = perSpan.Values.OrderBy(change => change.Span.Start).ToList();
			foreach (DocumentId documentId in baseSolution.GetDocumentIdsWithFilePath(path))
			{
				SourceText text = await baseSolution.GetDocument(documentId)!.GetTextAsync();
				updated = updated.WithDocumentText(documentId, text.WithChanges(ordered));
			}
		}

		foreach ((string razorPath, Dictionary<TextSpan, TextChange> perSpan) in razorChangesByPath)
		{
			List<TextChange> ordered = perSpan.Values.OrderBy(change => change.Span.Start).ToList();
			foreach (DocumentId documentId in baseSolution.GetDocumentIdsWithFilePath(razorPath))
			{
				if (baseSolution.GetAdditionalDocument(documentId) is not TextDocument additional)
					continue;

				SourceText text = await additional.GetTextAsync();
				updated = updated.WithAdditionalDocumentText(documentId, text.WithChanges(ordered));
			}
		}

		IReadOnlyList<string> changed = checkOnly
			? ApplyPipeline.GetChangedFilePaths(baseSolution, updated)
			: await ApplyPipeline.ApplyAsync(instance, updated);

		var builder = new OutlineBuilder();
		builder.Header("applied", !checkOnly);
		builder.Header("resolvedSymbol", resolvedName);
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, changed, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}

	/// <summary>
	/// Maps the changes that landed in Razor-generated documents back to their .razor/.cshtml sources,
	/// keyed by razor file path and deduped by span (the same razor edit arrives once per projection and
	/// per generated copy). Throws <see cref="RazorMappingException"/> when any change cannot be mapped
	/// and verified, so the caller aborts without a partial rename.
	/// </summary>
	private static async Task<Dictionary<string, Dictionary<TextSpan, TextChange>>> MapRazorGeneratedChangesAsync(
		Solution baseSolution,
		Dictionary<string, Dictionary<TextSpan, TextChange>> changesByPath)
	{
		var razorChangesByPath = new Dictionary<string, Dictionary<TextSpan, TextChange>>(StringComparer.OrdinalIgnoreCase);

		async Task<SourceText?> RazorTextFor(string razorPath)
		{
			foreach (DocumentId documentId in baseSolution.GetDocumentIdsWithFilePath(razorPath))
			{
				if (baseSolution.GetAdditionalDocument(documentId) is TextDocument additional)
					return await additional.GetTextAsync();
			}

			return null;
		}

		foreach ((string path, Dictionary<TextSpan, TextChange> perSpan) in changesByPath)
		{
			if (!RazorMapping.IsRazorGeneratedPath(path))
				continue;

			Document? generatedDocument = baseSolution.GetDocumentIdsWithFilePath(path)
				.Select(baseSolution.GetDocument)
				.FirstOrDefault(document => document is not null);
			if (generatedDocument is null)
				continue;

			List<TextChange> ordered = perSpan.Values.OrderBy(change => change.Span.Start).ToList();
			foreach ((string razorPath, TextChange change) in await RazorChangeMapper.MapChangesAsync(generatedDocument, ordered, RazorTextFor))
			{
				if (!razorChangesByPath.TryGetValue(razorPath, out Dictionary<TextSpan, TextChange>? razorPerSpan))
				{
					razorPerSpan = [];
					razorChangesByPath[razorPath] = razorPerSpan;
				}

				if (razorPerSpan.TryGetValue(change.Span, out TextChange existing) && !string.Equals(existing.NewText, change.NewText, StringComparison.Ordinal))
					throw new RazorMappingException(
						RazorMappingFailure.TextMismatch,
						path,
						$"Conflicting edits mapped to the same location in '{razorPath}'; the rename was not applied.");

				razorPerSpan[change.Span] = change;
			}
		}

		return razorChangesByPath;
	}
}
