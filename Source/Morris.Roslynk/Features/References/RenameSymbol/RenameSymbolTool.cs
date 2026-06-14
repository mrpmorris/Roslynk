using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.References.RenameSymbol;

[McpServerToolType]
public sealed class RenameSymbolTool
{
	public const string RenameSymbolName = "rename_symbol";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ApplyPipeline ApplyPipeline;

	public RenameSymbolTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
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
		"""
		Renames a symbol and all its references across the solution using Roslyn (correct across partial
		classes and code-behind; string literals and comments are left untouched). Refuses an invalid
		identifier and reports candidates when the name is ambiguous. Pass checkOnly to preview the
		files that would change without writing.
		""")]
	public async Task<RenameSymbolResult> RenameSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol to rename.")] string symbolName,
		[Description("The new name (must be a valid C# identifier).")] string newName,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		RenameSymbolResult Success(bool applied, string resolved, IReadOnlyList<string> changedFiles) =>
			new(model.SnapshotId, model.Status, error: null, applied, resolved, changedFiles);

		RenameSymbolResult Failure(Error error) =>
			new(model.SnapshotId, model.Status, error, applied: false, resolvedSymbol: null, changedFiles: null);

		if (!SyntaxFacts.IsValidIdentifier(newName))
			return Failure(Error.Invalid($"'{newName}' is not a valid C# identifier."));

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(solution, symbolName);
		if (matches.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}
		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous("The name is ambiguous; rename using one of the candidate fully-qualified names.", candidates));
		}

		ISymbol symbol = matches[0];
		string resolved = SymbolResolver.FullyQualifiedName(symbol);
		Solution renamed = await Renamer.RenameSymbolAsync(solution, symbol, new SymbolRenameOptions(), newName);

		if (checkOnly)
		{
			IReadOnlyList<string> preview = ApplyPipeline.GetChangedFilePaths(solution, renamed);
			return Success(applied: false, resolved, preview);
		}

		IReadOnlyList<string> changed = await ApplyPipeline.ApplyAsync(instance, renamed);
		return Success(applied: true, resolved, changed);
	}
}
