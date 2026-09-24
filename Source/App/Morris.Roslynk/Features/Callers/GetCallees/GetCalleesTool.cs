using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Callers.GetCallees;

[McpServerToolType]
public sealed class GetCalleesTool
{
	public const string GetCalleesName = "get_callees";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;

	public GetCalleesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = GetCalleesName,
		Title = "Get methods called by a method",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Finds the methods the resolved method invokes (by fully-qualified name).
		{OutlineDescriptions.CommonMethodInstructions}
		Callees are grouped file -> namespace -> containing type -> called member, each leaf showing the
		callee's declaration location:
		  resolvedSymbol=<name, parameter types included for a method or indexer>

		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>
		where kind is one of {OutlineDescriptions.KindList} and {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}.
		{OutlineDescriptions.Truncation} {OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} A callee is a method
		or constructor that is declared in source; a framework or metadata method (e.g. from the BCL) is not.
		An implicitly-synthesized constructor, such as a class's default constructor, is listed at the location
		of its declaring type. Calls inside a nested lambda, anonymous method or local function are attributed
		to that nested body, not to the resolved method. Prefer this over reading the body: it resolves each
		invocation through the compiler, so overloads and same-named methods are not confused.
		""")]
	public async Task<string> GetCallees(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description($"Fully-qualified name of the method, e.g. 'MyNamespace.MyType.MyMethod'. {OutlineDescriptions.SymbolNameGrammar}")] string methodName,
		[Description("Maximum callee locations to return.")] int maxResults = 100)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetCalleesCoreAsync(model, instance, methodName, maxResults, CancellationToken.None);
	}

	internal async Task<string> GetCalleesCoreAsync(SolutionModel model, RoslynInstance instance, string methodName, int maxResults = 100, CancellationToken token = default)
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

		// Union callees across every projection, deduped by stable symbol identity, so a callee that is only
		// invoked in a branch inactive in the loaded configuration is still reported, and a callee seen in
		// several projections (or invoked several times) is listed once.
		var seen = new HashSet<string>(StringComparer.Ordinal);
		var callees = new List<ISymbol>();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			if (projectionSymbol.Symbol is not IMethodSymbol method)
				continue;

			foreach (ISymbol callee in await CollectCalleesAsync(method, projectionSymbol.Projection.Solution, token))
			{
				if (seen.Add(ProjectionService.KeyOf(callee)))
					callees.Add(callee);
			}
		}

		// Deterministic order regardless of projection iteration order.
		IReadOnlyList<ISymbol> ordered = callees
			.OrderBy(callee => SymbolResolver.SignatureName(callee), StringComparer.Ordinal)
			.ToArray();

		IReadOnlyList<ISymbol> page = ordered.Take(Math.Max(0, maxResults)).ToArray();
		bool truncated = ordered.Count > page.Count;

		var root = new SymbolNode();
		foreach (ISymbol callee in page)
			SymbolPlacement.Place(root, callee, model.Solution, solutionDirectory);

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", SymbolResolver.SignatureName(resolved[0].Symbol));
		if (truncated)
		{
			builder.Header("count", ordered.Count);
			builder.Header("truncated", true);
		}
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}

	/// <summary>
	/// Collects the source-declared methods the body of <paramref name="method"/> invokes. Walks the
	/// declaration's syntax, resolving every call expression and object construction through the semantic
	/// model; a callee is a symbol that is a method and is declared in source, so a framework or metadata
	/// method is skipped. An implicitly-synthesized constructor (a class's default constructor) is reported
	/// because Roslyn assigns it a source location at its type declaration. Descending stops at a nested
	/// lambda, anonymous method or local function, so a call there is attributed to that inner body
	/// rather than to the resolved method.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> CollectCalleesAsync(IMethodSymbol method, Solution solution, CancellationToken token)
	{
		var found = new List<ISymbol>();
		foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
		{
			if (solution.GetDocument(reference.SyntaxTree) is not Document document)
				continue;

			SemanticModel? semanticModel = await document.GetSemanticModelAsync(token);
			if (semanticModel is null)
				continue;

			SyntaxNode declaration = await reference.GetSyntaxAsync(token);
			Collect(declaration, semanticModel, found, token);
		}

		return found;
	}

	private static void Collect(SyntaxNode node, SemanticModel semanticModel, List<ISymbol> found, CancellationToken token)
	{
		foreach (SyntaxNode child in node.ChildNodes())
		{
			// A nested method-like body owns its own invocations; they are not the resolved method's callees.
			if (child is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax or LocalFunctionStatementSyntax)
				continue;

			switch (child)
			{
				case InvocationExpressionSyntax invocation:
					Add(semanticModel.GetSymbolInfo(invocation, token).Symbol, found);
					break;
				case ObjectCreationExpressionSyntax creation:
					Add(semanticModel.GetSymbolInfo(creation, token).Symbol, found);
					break;
				case BaseObjectCreationExpressionSyntax creation:
					Add(semanticModel.GetSymbolInfo(creation, token).Symbol, found);
					break;
			}

			Collect(child, semanticModel, found, token);
		}
	}

	private static void Add(ISymbol? symbol, List<ISymbol> found)
	{
		if (symbol is IMethodSymbol method && method.Locations.Any(location => location.IsInSource))
			found.Add(method);
	}
}
