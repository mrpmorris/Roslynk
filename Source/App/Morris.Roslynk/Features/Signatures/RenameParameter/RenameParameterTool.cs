using System.Collections.Immutable;
using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
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

namespace Morris.Roslynk.Features.Signatures.RenameParameter;

[McpServerToolType]
public sealed class RenameParameterTool
{
	public const string RenameParameterName = "rename_parameter";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;
	private readonly ApplyPipeline ApplyPipeline;

	public RenameParameterTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[McpServerTool(
		Name = RenameParameterName,
		Title = "Rename a method parameter",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		$"""
		Renames one parameter of one method, constructor or indexer overload using Roslyn's semantic rename:
		the declaration, every use in the body, named arguments at call sites and <paramref> documentation
		references are updated; string literals, comments and same-named text elsewhere are left untouched.
		The rename cascades across the member's override/interface group — overridden and overriding members,
		interface members and their implementations, and both parts of a partial method — wherever the related
		declaration is in source and its parameter at the same position has the same old name. Related
		declarations that use a different name, or that live in metadata, are left as they are and reported in
		'unchangedRelated'. {OutlineDescriptions.ProjectionCoverage}
		Returns a text result, not JSON: 'applied' (N for a checkOnly preview), 'resolvedMethod' (the member
		as resolved, before the rename), 'parameter' (the old name), 'renamedMembers' (how many declarations
		had the parameter renamed, including the target), 'unchangedRelated' (semicolon-separated related
		members left alone; omitted when there are none), 'status' header, a blank line, then one
		solution-relative changed-file path per line. {OutlineDescriptions.Project} {OutlineDescriptions.Freshness}
		Errors: an invalid identifier, or a newName equal to parameterName, is error=Invalid; an ambiguous
		methodId returns one candidate= line per overload and a methodId that matches nothing returns fuzzy
		suggestions (copy a candidate back verbatim); a parameterName the member does not declare is
		error=NotFound listing its parameters; a newName already used by another parameter, local, local
		function, lambda/query variable or type parameter inside a renamed declaration is error=Conflict naming
		the member; a symbol that is not a method, constructor or indexer, a positional record parameter, or a
		member declared only in metadata is error=NotSupported; a file edited on disk since it was loaded is
		error=Stale. Nothing is written in any failure case. Pass checkOnly to preview the files that would
		change without writing.
		""")]
	public async Task<string> RenameParameter(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description($"Fully-qualified name of the method, constructor or indexer that declares the parameter. {OutlineDescriptions.SymbolNameGrammar}")] string methodId,
		[Description("The parameter's current name.")] string parameterName,
		[Description("The new name (must be a valid C# identifier).")] string newName,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (!SyntaxFacts.IsValidIdentifier(newName))
			return Failure(Error.Invalid($"'{newName}' is not a valid C# identifier."));
		if (string.Equals(parameterName, newName, StringComparison.Ordinal))
			return Failure(Error.Invalid($"The parameter is already named '{newName}'."));

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution baseSolution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(baseSolution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(baseSolution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, methodId, cancellationToken);
		if (groups.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(baseSolution, methodId, cancellationToken: cancellationToken);
			return Failure(Error.NotFound($"No symbol matched '{methodId}'.", suggestions.Count > 0 ? suggestions : null));
		}
		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(methodId, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];
		ISymbol member = resolved[0].Symbol;
		string resolvedName = SymbolResolver.SignatureName(member);

		if (ParametersOf(member) is not ImmutableArray<IParameterSymbol> parameters)
			return Failure(Error.NotSupported($"'{resolvedName}' is not a method, constructor or indexer."));
		if (!member.Locations.Any(location => location.IsInSource))
			return Failure(Error.NotSupported($"'{resolvedName}' is declared only in metadata, so its parameters cannot be renamed."));
		if (member is IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType.IsRecord: true } constructor
			&& constructor.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(cancellationToken) is RecordDeclarationSyntax))
		{
			return Failure(Error.NotSupported($"'{resolvedName}' is a positional record constructor; renaming its parameter would also rename the generated property. Use rename_symbol on the property instead."));
		}

		int ordinal = -1;
		for (int index = 0; index < parameters.Length && ordinal < 0; index++)
		{
			if (string.Equals(parameters[index].Name, parameterName, StringComparison.Ordinal))
				ordinal = index;
		}
		if (ordinal < 0)
		{
			string declared = parameters.Length == 0 ? "it has no parameters" : $"its parameters are {string.Join(", ", parameters.Select(parameter => parameter.Name))}";
			return Failure(Error.NotFound($"'{resolvedName}' has no parameter named '{parameterName}'; {declared}."));
		}

		var targets = new List<RenameTarget>();
		var renamedMembers = new HashSet<string>(StringComparer.Ordinal);
		var unchangedRelated = new SortedSet<string>(StringComparer.Ordinal);
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			Solution projectionSolution = projectionSymbol.Projection.Solution;
			foreach (ISymbol related in await RelatedMembersAsync(projectionSymbol.Symbol, projectionSolution, cancellationToken))
			{
				ImmutableArray<IParameterSymbol> relatedParameters = ParametersOf(related) ?? [];
				bool inSource = related.Locations.Any(location => location.IsInSource);
				if (!inSource || ordinal >= relatedParameters.Length || !string.Equals(relatedParameters[ordinal].Name, parameterName, StringComparison.Ordinal))
				{
					unchangedRelated.Add(SymbolResolver.SignatureName(related));
					continue;
				}

				string? conflict = await FindConflictAsync(related, newName, projectionSolution, cancellationToken);
				if (conflict is not null)
					return Failure(Error.Conflict($"Cannot rename '{parameterName}' to '{newName}' in '{SymbolResolver.SignatureName(related)}': {conflict}"));

				renamedMembers.Add(ProjectionService.KeyOf(related));
				targets.Add(new RenameTarget(projectionSolution, relatedParameters[ordinal]));
			}
		}

		Solution updated;
		try
		{
			updated = await ProjectionRenamer.RenameAsync(baseSolution, targets, newName, cancellationToken);
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
		builder.Header("resolvedMethod", resolvedName);
		builder.Header("parameter", parameterName);
		builder.Header("renamedMembers", renamedMembers.Count);
		if (unchangedRelated.Count > 0)
			builder.Header("unchangedRelated", string.Join("; ", unchangedRelated));
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, changed, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}

	private static ImmutableArray<IParameterSymbol>? ParametersOf(ISymbol symbol) =>
		symbol switch
		{
			IMethodSymbol method when method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.ExplicitInterfaceImplementation
				or MethodKind.UserDefinedOperator or MethodKind.Conversion => method.Parameters,
			IPropertySymbol { IsIndexer: true } indexer => indexer.Parameters,
			_ => null
		};

	/// <summary>
	/// The member plus every declaration whose parameter list must stay in step with it: the members it
	/// overrides or implements, the members that override or implement it, and the other part of a partial
	/// method — followed transitively, so a whole override/interface family is found from any member of it.
	/// </summary>
	private static async Task<IReadOnlyList<ISymbol>> RelatedMembersAsync(ISymbol member, Solution solution, CancellationToken cancellationToken)
	{
		var found = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
		var ordered = new List<ISymbol>();
		var pending = new Queue<ISymbol>();

		void Add(ISymbol? symbol)
		{
			if (symbol is null)
				return;

			ISymbol definition = symbol.OriginalDefinition;
			if (found.Add(definition))
			{
				ordered.Add(definition);
				pending.Enqueue(definition);
			}
		}

		Add(member);
		while (pending.Count > 0)
		{
			ISymbol current = pending.Dequeue();
			switch (current)
			{
				case IMethodSymbol method:
					Add(method.PartialDefinitionPart);
					Add(method.PartialImplementationPart);
					Add(method.OverriddenMethod);
					break;

				case IPropertySymbol property:
					Add(property.OverriddenProperty);
					break;
			}

			if (current is IMethodSymbol { MethodKind: MethodKind.Constructor })
				continue;

			foreach (ISymbol implemented in await SymbolFinder.FindImplementedInterfaceMembersAsync(current, solution, cancellationToken: cancellationToken))
				Add(implemented);
			foreach (ISymbol overriding in await SymbolFinder.FindOverridesAsync(current, solution, cancellationToken: cancellationToken))
				Add(overriding);
			if (current.ContainingType?.TypeKind == TypeKind.Interface)
			{
				foreach (ISymbol implementation in await SymbolFinder.FindImplementationsAsync(current, solution, cancellationToken: cancellationToken))
					Add(implementation);
			}
		}

		return ordered;
	}

	/// <summary>
	/// Describes the first symbol named <paramref name="newName"/> declared inside the member's declarations
	/// (another parameter, a local, local function, lambda or query variable, or a type parameter), which the
	/// renamed parameter would collide with or be captured by; or null when the name is free.
	/// </summary>
	private static async Task<string?> FindConflictAsync(ISymbol member, string newName, Solution solution, CancellationToken cancellationToken)
	{
		foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
		{
			SyntaxNode declaration = await reference.GetSyntaxAsync(cancellationToken);
			Document? document = solution.GetDocument(declaration.SyntaxTree);
			if (document is null)
				continue;

			SemanticModel? semanticModel = await document.GetSemanticModelAsync(cancellationToken);
			if (semanticModel is null)
				continue;

			// A primary constructor's declaring syntax is the whole type; only its parameter list belongs to it.
			SyntaxNode scope = declaration is TypeDeclarationSyntax { ParameterList: { } primaryParameters } ? primaryParameters : declaration;
			foreach (SyntaxNode node in scope.DescendantNodesAndSelf())
			{
				ISymbol? declared = semanticModel.GetDeclaredSymbol(node, cancellationToken);
				if (declared is null || !string.Equals(declared.Name, newName, StringComparison.Ordinal))
					continue;

				string? kind = declared switch
				{
					IParameterSymbol => "a parameter",
					ILocalSymbol => "a local variable",
					IRangeVariableSymbol => "a query variable",
					ITypeParameterSymbol => "a type parameter",
					IMethodSymbol { MethodKind: MethodKind.LocalFunction } => "a local function",
					_ => null
				};
				if (kind is not null)
					return $"{kind} named '{newName}' is already declared there.";
			}
		}

		return null;
	}
}
