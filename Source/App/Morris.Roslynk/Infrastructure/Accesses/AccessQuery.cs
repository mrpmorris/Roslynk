using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Accesses;

/// <summary>
/// The shared engine behind find_reads and find_writes: resolves a field, property or parameter across every
/// projection, classifies each reference (plus declaration initialisers) by <see cref="AccessKind"/>, keeps
/// the kinds the caller asked for, and renders them in the find_references outline shape with the access
/// kind as a trailing field on each leaf.
/// </summary>
public static class AccessQuery
{
	/// <summary>Separates a member's name from one of its parameters: 'N.T.M:param'.</summary>
	public const char ParameterSeparator = ':';

	public static async Task<string> RunAsync(
		SolutionModel model,
		SymbolResolver symbolResolver,
		ProjectionService projectionService,
		string symbolName,
		int maxResults,
		Func<AccessKind, bool> include,
		CancellationToken cancellationToken)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		(string memberName, string? parameterName) = Split(symbolName);
		if (parameterName is not null && (parameterName.Length == 0 || memberName.Length == 0))
			return Failure(Error.Invalid($"'{symbolName}' is not a valid name; a parameter is written 'Namespace.Type.Member:parameterName'."));

		IReadOnlyList<Projection> projections = await projectionService.BuildAsync(solution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await projectionService.ResolveAsync(symbolResolver, projections, memberName, cancellationToken);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await symbolResolver.SuggestAsync(solution, memberName, cancellationToken: cancellationToken);
			return Failure(Error.NotFound($"No symbol matched '{memberName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(memberName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		string resolvedName = SymbolResolver.SignatureName(resolved[0].Symbol);

		if (parameterName is not null)
		{
			ISymbol member = resolved[0].Symbol;
			if (ParametersOf(member) is null)
				return Failure(Error.NotSupported($"'{resolvedName}' is a {SymbolKindText.Of(member)}, which has no parameters."));

			var parameters = new List<ProjectionSymbol>();
			foreach (ProjectionSymbol projectionSymbol in resolved)
			{
				IParameterSymbol? parameter = ParametersOf(projectionSymbol.Symbol)?.FirstOrDefault(p => p.Name == parameterName);
				if (parameter is not null)
					parameters.Add(new ProjectionSymbol(projectionSymbol.Projection, parameter));
			}

			if (parameters.Count == 0)
			{
				if (await DeclaresLocalAsync(member, parameterName, cancellationToken))
					return Failure(Error.NotSupported($"'{parameterName}' is a local variable of '{resolvedName}'; local variables are not supported, only fields, properties and parameters."));

				IReadOnlyList<string> candidates = ParametersOf(member)!.Select(p => $"{resolvedName}{ParameterSeparator}{p.Name}").ToArray();
				return Failure(Error.NotFound($"'{resolvedName}' has no parameter named '{parameterName}'.", candidates.Count > 0 ? candidates : null));
			}

			resolved = parameters;
			resolvedName = $"{resolvedName}{ParameterSeparator}{parameterName}";
		}
		else if (resolved[0].Symbol is not (IFieldSymbol or IPropertySymbol))
		{
			return Failure(Error.NotSupported(
				$"'{resolvedName}' is a {SymbolKindText.Of(resolved[0].Symbol)}; only fields, properties and parameters "
				+ $"('Namespace.Type.Member{ParameterSeparator}parameterName') can be read or written."));
		}

		var seen = new HashSet<string>(StringComparer.Ordinal);
		var hits = new List<(Location Location, Solution Solution, AccessKind Kind)>();

		void Add(Location location, Solution locationSolution, AccessKind kind)
		{
			if (!include(kind))
				return;

			FileLinePositionSpan span = location.GetDisplaySpan();
			if (seen.Add($"{span.Path}|{span.StartLinePosition}-{span.EndLinePosition}|{kind}"))
				hits.Add((location, locationSolution, kind));
		}

		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			Solution projectionSolution = projectionSymbol.Projection.Solution;
			ISymbol symbol = projectionSymbol.Symbol;

			foreach (Location initializer in DeclarationInitializers(symbol, cancellationToken))
				Add(initializer, projectionSolution, AccessKind.Init);

			foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, projectionSolution, cancellationToken))
			{
				foreach (ReferenceLocation reference in referenced.Locations)
				{
					if (!reference.Location.IsInSource || reference.Location.SourceTree is not SyntaxTree tree)
						continue;

					SemanticModel? semanticModel = await reference.Document.GetSemanticModelAsync(cancellationToken);
					if (semanticModel is null)
						continue;

					SyntaxNode root = await tree.GetRootAsync(cancellationToken);
					SyntaxNode node = root.FindNode(reference.Location.SourceSpan, getInnermostNodeForTie: true);
					if (AccessClassifier.Classify(node, symbol, semanticModel, cancellationToken) is AccessKind kind)
						Add(reference.Location, projectionSolution, kind);
				}
			}
		}

		hits = hits
			.OrderBy(hit => hit.Location.GetDisplaySpan().Path, StringComparer.Ordinal)
			.ThenBy(hit => hit.Location.GetDisplaySpan().StartLinePosition)
			.ToList();
		List<(Location Location, Solution Solution, AccessKind Kind)> page = hits.Take(Math.Max(0, maxResults)).ToList();
		bool truncated = hits.Count > page.Count;

		var outline = new SymbolNode();
		foreach ((Location location, Solution locationSolution, AccessKind kind) in page)
		{
			EnclosingPath enclosing = await EnclosingDeclaration.ResolveAsync(locationSolution, location, cancellationToken);
			FileLinePositionSpan span = location.GetDisplaySpan();

			SymbolNode fileParent = location.SourceTree is SyntaxTree tree && ProjectName.Of(locationSolution, tree) is string project
				? outline.Child(project)
				: outline;
			SymbolNode node = fileParent
				.ChildPath(SolutionRelativePath.Of(solutionDirectory, span.Path)!)
				.Child(enclosing.Namespace);
			for (int index = 0; index < enclosing.Segments.Count - 1; index++)
				node = node.Child($"{enclosing.Segments[index].Kind},{OutlineBuilder.Field(enclosing.Segments[index].Name)}");

			EnclosingSegment leaf = enclosing.Segments[^1];
			node.AddLeaf(
				$"{leaf.Kind},{OutlineBuilder.Field(leaf.Name)}",
				span.StartLinePosition.Line + 1,
				span.StartLinePosition.Character + 1,
				span.EndLinePosition.Line + 1,
				span.EndLinePosition.Character + 1,
				AccessKindText.Of(kind));
		}

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", resolvedName);
		if (truncated)
		{
			builder.Header("count", hits.Count);
			builder.Header("truncated", true);
		}
		builder.Status(model.Status);
		builder.BeginBody();
		outline.Render(builder);
		return builder.ToString();
	}

	/// <summary>Splits 'N.T.M:param' at the last single ':' outside any bracket; '::' (global::) is not a separator.</summary>
	internal static (string Member, string? Parameter) Split(string symbolName)
	{
		int depth = 0;
		for (int index = symbolName.Length - 1; index >= 0; index--)
		{
			char c = symbolName[index];
			if (c is ')' or ']' or '>')
				depth++;
			else if (c is '(' or '[' or '<')
				depth--;
			else if (c == ParameterSeparator && depth == 0)
			{
				bool doubled = (index > 0 && symbolName[index - 1] == ':') || (index + 1 < symbolName.Length && symbolName[index + 1] == ':');
				if (!doubled)
					return (symbolName[..index].Trim(), symbolName[(index + 1)..].Trim());
			}
		}

		return (symbolName, null);
	}

	private static IReadOnlyList<IParameterSymbol>? ParametersOf(ISymbol symbol) =>
		symbol switch
		{
			IMethodSymbol method => method.Parameters,
			IPropertySymbol property => property.Parameters,
			_ => null,
		};

	private static async Task<bool> DeclaresLocalAsync(ISymbol member, string name, CancellationToken cancellationToken)
	{
		foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
		{
			SyntaxNode syntax = await reference.GetSyntaxAsync(cancellationToken);
			foreach (SyntaxNode node in syntax.DescendantNodes())
			{
				SyntaxToken identifier = node switch
				{
					VariableDeclaratorSyntax declarator => declarator.Identifier,
					SingleVariableDesignationSyntax designation => designation.Identifier,
					ForEachStatementSyntax forEach => forEach.Identifier,
					CatchDeclarationSyntax catchDeclaration => catchDeclaration.Identifier,
					_ => default,
				};
				if (identifier.ValueText == name)
					return true;
			}
		}

		return false;
	}

	/// <summary>'int F = 1;' and 'int P { get; } = 1;' write the value without referencing the symbol.</summary>
	private static IEnumerable<Location> DeclarationInitializers(ISymbol symbol, CancellationToken cancellationToken)
	{
		if (symbol is not (IFieldSymbol or IPropertySymbol))
			yield break;

		foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
		{
			SyntaxNode syntax = reference.GetSyntax(cancellationToken);
			switch (syntax)
			{
				case VariableDeclaratorSyntax { Initializer: not null } declarator:
					yield return declarator.Identifier.GetLocation();
					break;
				case PropertyDeclarationSyntax { Initializer: not null } property:
					yield return property.Identifier.GetLocation();
					break;
			}
		}
	}
}
