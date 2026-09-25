using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Renaming;
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
		Renames a symbol and all its references across the solution using Roslyn, resolved by the
		fully-qualified symbolName (an overload is targeted by its parameter-type list). The rename is correct
		across partial classes, code-behind, every #if/#else branch and every target framework; string
		literals and comments are left untouched.
		{OutlineDescriptions.ProjectionCoverage}
		Symbols declared or referenced in .razor/.cshtml files are renamed by rewriting those files directly
		— edits computed against the Razor-generated code are mapped back through the compiler's #line
		directives, covering @code blocks, markup expressions, and component-attribute usages in other
		components' markup. If a Razor edit cannot be mapped and verified, the rename aborts before anything
		is written (error=Conflict or error=NotSupported). If a file the rename changes was edited (on disk or
		in the loaded model) after the rename was computed, nothing is written and error=Stale names the file;
		edits to other files made in the meantime are kept.
		Returns a text result, not JSON: 'applied' (N for a checkOnly preview), 'resolvedSymbol' (the name
		as resolved, before the rename), 'status' header, a blank line, then one solution-relative
		changed-file path per line. {OutlineDescriptions.Project} {OutlineDescriptions.Freshness}
		Errors: an invalid identifier is refused; an ambiguous name returns one candidate= line per match,
		and a name that matches nothing returns fuzzy suggestions — in either case copy a candidate back
		verbatim to target a single symbol. Pass checkOnly to preview the files that would change without
		writing. Prefer this over a textual find/replace rename; it renames the symbol itself, so it never
		touches unrelated same-named text.
		""")]
	public async Task<string> RenameSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description($"Fully-qualified name of the symbol to rename. {OutlineDescriptions.SymbolNameGrammar}")] string symbolName,
		[Description("The new name (must be a valid C# identifier).")] string newName,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
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

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(baseSolution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName, cancellationToken);
		if (groups.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(baseSolution, symbolName, cancellationToken: cancellationToken);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}
		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(symbolName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		string resolvedName = SymbolResolver.SignatureName(resolved[0].Symbol);

		Solution updated;
		try
		{
			updated = await ProjectionRenamer.RenameAsync(
				baseSolution,
				resolved.Select(projectionSymbol => new RenameTarget(projectionSymbol.Projection.Solution, projectionSymbol.Symbol)),
				newName,
				cancellationToken);
		}
		catch (RazorMappingException exception)
		{
			return Failure(exception.Kind == RazorMappingFailure.TextMismatch
				? Error.Conflict(exception.Message)
				: Error.NotSupported(exception.Message));
		}
		catch (RenameConflictException exception)
		{
			return Failure(Error.Conflict(exception.Message));
		}

		IReadOnlyList<string> changed;
		if (checkOnly)
		{
			changed = ApplyPipeline.GetChangedFilePaths(baseSolution, updated);
		}
		else
		{
			try
			{
				changed = await ApplyPipeline.ApplyAsync(instance, updated, basedOn: baseSolution, cancellationToken);
			}
			catch (StaleWriteException exception)
			{
				return OutlineError.Format(
					Error.Stale(exception.Message, [SolutionRelativePath.Of(solutionDirectory, exception.FilePath) ?? exception.FilePath]),
					instance.CurrentModel.Status);
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("applied", !checkOnly);
		builder.Header("resolvedSymbol", resolvedName);
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, changed, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}
}
