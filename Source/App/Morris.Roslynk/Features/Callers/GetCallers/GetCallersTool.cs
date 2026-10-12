using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Callers;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Callers.GetCallers;

[McpServerToolType]
public sealed class GetCallersTool
{
	public const string GetCallersName = "get_callers";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ResponseBudget Budget;

	public GetCallersTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[McpServerTool(
		Name = GetCallersName,
		Title = "Get callers of a method",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Finds the methods that call the named method. Calls the compiler inserts count too: foreach/await
		plumbing (GetEnumerator, MoveNext, Current, an enumerator's Dispose, await foreach and await using
		equivalents, GetAwaiter, IsCompleted, GetResult), the Dispose/DisposeAsync a using or await using
		runs, deconstruction and positional patterns, collection-initializer Add, user-defined operators
		and conversions (including +=, ++, implicit conversions and true/false in a condition),
		query-expression methods, interpolated-string handler members, implicit base() calls, and the
		Length/Count behind ^ and ...
		Callers are grouped file -> namespace -> containing type -> calling member, each leaf showing the
		caller's declaration location:
		  resolvedSymbol=<name, parameter types included for a method or indexer>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>
		""")]
	public async Task<string> GetCallers(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Fully-qualified method name; reuse names returned by discovery. Include parameter types to select one overload.")] string methodName,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetCallersCoreAsync(model, instance, methodName, startItem, maxSize, Budget, CancellationToken.None);
	}

	internal async Task<string> GetCallersCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		string methodName,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, methodName, token);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(model.Solution, methodName);
			return Failure(Error.NotFound($"No symbol matched '{methodName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(methodName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union callers across every projection, deduped by stable symbol identity, so a caller that only
		// compiles in a branch inactive in the loaded configuration is still reported. FindCallersAsync finds
		// explicit calls and the compiler-inserted calls Roslyn indexes (foreach GetEnumerator/MoveNext/Current,
		// await GetAwaiter, using Dispose, deconstruction, collection-initializer Add, operator tokens incl.
		// += and ++, implicit base()); ImplicitCallerScan adds the rest, gated on the target's shape so
		// ordinary targets pay nothing. The copy kept is the best bound, so the placement always describes a
		// signature that bound.
		var callers = new DeclarationCopies<Solution>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			Solution solution = projectionSymbol.Projection.Solution;

			foreach (SymbolCallerInfo caller in await SymbolFinder.FindCallersAsync(projectionSymbol.Symbol, solution, token))
			{
				if (await IsOnlySpuriousDisposeAsync(caller, token))
					continue;

				callers.Add(CallerSymbol.Normalize(caller.CallingSymbol), solution);
			}

			foreach (ISymbol caller in await ImplicitCallerScan.CallersAsync(
				projectionSymbol.Symbol,
				projectionSymbol.Projection,
				isBaseProjection: ReferenceEquals(projectionSymbol.Projection, resolved[0].Projection),
				token))
			{
				callers.Add(caller, solution);
			}
		}

		var root = new SymbolNode();
		foreach ((ISymbol caller, Solution callerSolution) in callers.Items)
			SymbolPlacement.Place(root, caller, callerSolution, solutionDirectory);

		PagedRender render = ItemPaging.Render(
			model.Status,
			builder => builder.Header("resolvedSymbol", SymbolResolver.SignatureName(resolved[0].Symbol)),
			builder => root.Render(builder),
			root.Items(),
			startItem,
			maxSize,
			budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => Failure(Error.Invalid(invalid.Message)),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}

	/// <summary>
	/// Roslyn's using-disposal finder reports every single-variable local declaration of a disposable type,
	/// with or without 'using' (Resource r = new Resource(); "calls" Resource.Dispose), at the declaration's
	/// type token; a real using is reported at its 'using' keyword and an explicit call at the name, so a
	/// location inside the type span of a declaration with no 'using' keyword is never a call. A caller is
	/// dropped only when every one of its locations is such a declaration. Roslyn 5.9 behavior; the filter is
	/// a no-op once fixed upstream.
	/// </summary>
	private static async Task<bool> IsOnlySpuriousDisposeAsync(SymbolCallerInfo caller, CancellationToken cancellationToken)
	{
		if (caller.CalledSymbol is not IMethodSymbol { Name: WellKnownMemberNames.DisposeMethodName or WellKnownMemberNames.DisposeAsyncMethodName })
			return false;

		foreach (Location location in caller.Locations)
		{
			if (location.SourceTree is not SyntaxTree tree)
				return false;

			SyntaxNode node = (await tree.GetRootAsync(cancellationToken)).FindNode(location.SourceSpan, getInnermostNodeForTie: true);
			if (node.FirstAncestorOrSelf<LocalDeclarationStatementSyntax>() is not { } declaration
				|| !declaration.UsingKeyword.IsKind(SyntaxKind.None)
				|| !declaration.Declaration.Type.Span.Contains(location.SourceSpan))
			{
				return false;
			}
		}

		return true;
	}
}
