using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Morris.Roslynk.Infrastructure.Observability;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>
/// The callers of a member through calls the compiler inserts that SymbolFinder.FindCallersAsync does not
/// find (see <see cref="ImplicitCallTargets"/>). Only documents of projects that can see the target are
/// read; a sound text filter, then a syntax filter, picks the sites that could hide such a call; each
/// site's operation tree runs through <see cref="CallCollector"/>, so a caller is reported exactly when
/// get_callees on it would list the target.
/// </summary>
internal static class ImplicitCallerScan
{
	private const int MaxParallelism = 4;

	public static async Task<IReadOnlyList<ISymbol>> CallersAsync(
		ISymbol target,
		Projection projection,
		bool isBaseProjection,
		CancellationToken cancellationToken)
	{
		ImplicitCallScan scans = ImplicitCallTargets.ScansFor(target);
		if (scans == ImplicitCallScan.None || CalleePlacement.IsExternal(target))
			return [];

		using (Activity? activity = RoslynkActivitySource.Instance.StartActivity("implicit_caller_scan"))
		{
			activity?.SetTag("roslynk.scan.kinds", scans.ToString());
			Solution solution = projection.Solution;
			TargetIdentity identity = TargetIdentity.Of(target);
			var callers = new ConcurrentDictionary<ISymbol, byte>(SymbolEqualityComparer.Default);
			IReadOnlyList<Document> documents = await CandidateDocuments(target, solution, cancellationToken);

			await Parallel.ForEachAsync(
				documents,
				new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
				async (document, token) =>
				{
					SourceText? text = await document.GetTextAsync(token);
					if (text is null || !ImplicitCallTargets.MayContain(text, scans))
						return;

					if (await document.GetSyntaxRootAsync(token) is not CSharpSyntaxNode root)
						return;

					// A variant projection only re-parses trees whose directives differ; the base projection
					// already scanned every directive-free tree.
					if (!isBaseProjection && !root.ContainsDirectives)
						return;

					IReadOnlyList<SyntaxNode> sites = ImplicitCallTargets.Sites(root, scans);
					SemanticModel? model = await document.GetSemanticModelAsync(token);
					if (model is null)
						return;

					foreach (SyntaxNode site in sites)
					{
						if (Anchor(model.GetOperation(site, token)) is not IOperation operation)
							continue;

						bool calls = false;
						new CallCollector((callee, _) => calls |= identity.Matches(callee)).Visit(operation);
						if (!calls)
							continue;

						if (CallerSymbol.Of(model, site, token) is ISymbol caller)
							callers.TryAdd(CallerSymbol.Normalize(caller), 0);
					}

					if (scans.HasFlag(ImplicitCallScan.BaseConstructor))
						FindDerivedConstructorCallers(model, root, target, callers, token);
				});

			activity?.SetTag("roslynk.scan.documents", documents.Count);
			activity?.SetTag("roslynk.scan.callers", callers.Count);
			return [.. callers.Keys];
		}
	}

	/// <summary>
	/// A derived constructor without an initializer calls base() implicitly, and Roslyn's reference finders do
	/// not report it (verified against 5.9.0, where the plan's confirm-by-test row said otherwise). Each
	/// explicit constructor of a derived type with no base(...)/this(...) initializer is a caller; implicitly
	/// declared constructors have no syntax and are not reported.
	/// </summary>
	private static void FindDerivedConstructorCallers(SemanticModel model, SyntaxNode root, ISymbol target, ConcurrentDictionary<ISymbol, byte> callers, CancellationToken token)
	{
		if (target is not IMethodSymbol { MethodKind: MethodKind.Constructor } constructor || constructor.ContainingType is not INamedTypeSymbol baseType)
			return;

		string baseTypeName = Resolution.SymbolResolver.FullyQualifiedName(baseType);

		foreach (TypeDeclarationSyntax typeDeclaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
		{
			if (model.GetDeclaredSymbol(typeDeclaration, token) is not INamedTypeSymbol type || !DerivesFrom(type, baseTypeName))
				continue;

			foreach (IMethodSymbol candidate in type.InstanceConstructors)
			{
				if (candidate.IsImplicitlyDeclared || candidate.DeclaringSyntaxReferences.Length == 0)
					continue;

				switch (candidate.DeclaringSyntaxReferences[0].GetSyntax(token))
				{
					// A constructor with no initializer calls base() directly; one with base(...) or this(...)
					// is either an explicit reference or chained elsewhere.
					case ConstructorDeclarationSyntax { Initializer: null }:
						callers.TryAdd(CallerSymbol.Normalize(candidate), 0);
						break;

					// A primary constructor declares through its type; it calls base() implicitly unless the
					// base list carries arguments.
					case TypeDeclarationSyntax declaration when !HasBaseArguments(declaration):
						callers.TryAdd(CallerSymbol.Normalize(candidate), 0);
						break;
				}
			}
		}
	}

	private static bool DerivesFrom(INamedTypeSymbol? type, string baseTypeName)
	{
		for (INamedTypeSymbol? current = type?.BaseType; current is not null; current = current.BaseType)
		{
			if (string.Equals(Resolution.SymbolResolver.FullyQualifiedName(current), baseTypeName, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	private static bool HasBaseArguments(TypeDeclarationSyntax typeDeclaration) =>
		typeDeclaration.BaseList?.Types
			.OfType<PrimaryConstructorBaseTypeSyntax>()
			.Any(baseType => baseType.ArgumentList?.Arguments.Count > 0) == true;

	/// <summary>
	/// Climbs from the operation bound to a site through implicit wrappers sharing its syntax (a handler
	/// creation around an interpolated string, a conversion around an expression), so the walk sees the call
	/// the compiler hung on that syntax.
	/// </summary>
	private static IOperation? Anchor(IOperation? operation)
	{
		while (operation?.Parent is IOperation parent && parent.Syntax == operation.Syntax)
			operation = parent;

		return operation;
	}

	/// <summary>The declaring project and every project depending on it; source-generated documents included.</summary>
	private static async Task<IReadOnlyList<Document>> CandidateDocuments(ISymbol target, Solution solution, CancellationToken cancellationToken)
	{
		Project? declaring = null;
		foreach (Project project in solution.Projects)
		{
			Compilation? compilation = await project.GetCompilationAsync(cancellationToken);
			if (compilation is not null
				&& target.ContainingAssembly is not null
				&& SymbolEqualityComparer.Default.Equals(compilation.Assembly, target.ContainingAssembly))
			{
				declaring = project;
				break;
			}
		}

		if (declaring is null)
			return [];

		var documents = new List<Document>();
		ProjectDependencyGraph graph = solution.GetProjectDependencyGraph();
		foreach (ProjectId id in graph.GetProjectsThatTransitivelyDependOnThisProject(declaring.Id).Prepend(declaring.Id))
		{
			Project project = solution.GetProject(id)!;
			documents.AddRange(project.Documents);
			documents.AddRange(await project.GetSourceGeneratedDocumentsAsync(cancellationToken));
		}

		return documents;
	}
}
