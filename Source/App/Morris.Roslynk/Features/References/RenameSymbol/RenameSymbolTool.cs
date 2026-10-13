using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
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

	[Description("""
		Renames a symbol and its references across partial classes, code-behind and loaded frameworks,
		subject to the shared conditional-coverage limits. Strings and comments are unchanged.

		Shared Razor mapping rules cover @code, markup expressions and component attributes;
		unverified/unmappable edits abort before saving (Conflict or NotSupported). Generated-only
		declarations return NotSupported naming the generator - change the generator or its input
		instead; partial members/types with handwritten declarations are renamable because the generator
		follows their names.

		Returns applied (N for checkOnly), resolvedSymbol (the pre-rename name) and status headers, then
		a blank line and solution-relative changed paths.

		- Invalid: invalid identifier.
		- Ambiguous: one candidate per match; with no match, fuzzy suggestions. Reuse candidates
		  verbatim.
		- Stale: a target changed on disk or in the model since computation, naming the file;
		  intervening edits to other files are preserved. Stale detection occurs before saving.
		""")]
	[McpServerTool(
		Name = RenameSymbolName,
		Title = "Rename a symbol",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	public async Task<string> RenameSymbol(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Fully-qualified symbol name; reuse names returned by discovery. Include parameter types to select one overload.")] string symbolName,
		[Description("The new name (must be a valid C# identifier).")] string newName,
		[Description(ParameterDescriptions.CheckOnly)] bool checkOnly = false,
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

		// The rename would only reach references: a generated declaration is never written (and the
		// generator re-emits the old name), so the result would break the build rather than rename anything.
		if (resolved.Any(projectionSymbol => GeneratedSource.IsGeneratedOnly(projectionSymbol.Projection.Solution, projectionSymbol.Symbol)))
			return Failure(Error.NotSupported(GeneratedSource.GeneratedOnlyMessage(resolved[0].Projection.Solution, resolved[0].Symbol, resolvedName, "renamed")));

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
