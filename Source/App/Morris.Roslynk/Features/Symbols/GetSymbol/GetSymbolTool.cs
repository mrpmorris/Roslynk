using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.GetSymbol;

[McpServerToolType]
public sealed class GetSymbolTool
{
	public const string GetSymbolName = "get_symbol";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;

	public GetSymbolTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = GetSymbolName,
		Title = "Get symbol details",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Returns a symbol's declaration, resolved by fully-qualified name. {OutlineDescriptions.TextNotJson} A
		single source match returns a '#project=<project>', '#path=<relative/path.cs>' and
		'#loc=<startLine:startCol-endLine:endCol>' header, a blank line, then the verbatim declaration cut
		before its body (the opening brace or '=>'). The declaration line itself conveys accessibility, kind,
		return type, name and parameters, so those are not repeated. Example:
		  #project=VendmanagerWeb
		  #path=VendmanagerWeb/Components/Pages/Ops/TaskManager/TaskManager.razor.cs
		  #loc=196:5-214:6

		  private Task Search(CancellationToken cancellationToken)
		A metadata symbol (no source) instead returns '#source=metadata', '#kind', '#signature', '#assembly'.
		An ambiguous name returns a 'project -> file -> namespace -> containing type(s) -> kind,name,loc' locator tree to
		disambiguate. {OutlineDescriptions.Project}. {OutlineDescriptions.ErrorBlock} Prefer this over reading the file to identify a symbol.
		""")]
	public async Task<string> GetSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve across every projection (with metadata fallback) so a symbol declared only in a branch
		// inactive in the loaded configuration is still found; group by stable identity.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		var bySymbol = new Dictionary<string, (ISymbol Symbol, Solution Solution)>(StringComparer.Ordinal);
		var order = new List<string>();
		foreach (Projection projection in projections)
		{
			foreach (ISymbol candidate in await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(projection.Solution, symbolName, cancellationToken))
			{
				string key = ProjectionService.KeyOf(candidate);
				if (bySymbol.TryAdd(key, (candidate, projection.Solution)))
					order.Add(key);
			}
		}

		if (order.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(model.Solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}

		if (order.Count > 1)
			return Locator(order.Select(key => bySymbol[key]).ToList(), model, solutionDirectory);

		(ISymbol Symbol, Solution Solution) match = bySymbol[order[0]];
		return await DetailLeanAsync(match.Symbol, match.Solution, solutionDirectory, cancellationToken);
	}

	private async Task<string> DetailLeanAsync(ISymbol symbol, Solution solution, string? solutionDirectory, CancellationToken cancellationToken)
	{
		SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
		if (reference is null)
			return MetadataLean(symbol);

		FileLinePositionSpan span = reference.SyntaxTree.GetDisplaySpan(reference.Span);
		SourceText text = await reference.SyntaxTree.GetTextAsync(cancellationToken);
		SyntaxNode node = await reference.GetSyntaxAsync(cancellationToken);

		var builder = new OutlineBuilder();
		if (ProjectName.Of(solution, reference.SyntaxTree) is string project)
			builder.Header("project", project);
		builder.Header("path", SolutionRelativePath.Of(solutionDirectory, span.Path)!);
		builder.Header("loc", $"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}");
		builder.BeginBody();
		builder.Line(0, DeclarationHeader(node, text));
		return builder.ToString();
	}

	private static string MetadataLean(ISymbol symbol)
	{
		var builder = new OutlineBuilder();
		builder.Header("source", "metadata");
		builder.Header("kind", SymbolKindText.Of(symbol));
		builder.Header("signature", symbol.ToDisplayString());
		if (symbol.ContainingAssembly is { } assembly)
			builder.Header("assembly", assembly.Name);

		return builder.ToString();
	}

	private static string Locator(IReadOnlyList<(ISymbol Symbol, Solution Solution)> matches, SolutionModel model, string? solutionDirectory)
	{
		var root = new SymbolNode();
		foreach ((ISymbol symbol, Solution solution) in matches)
			SymbolPlacement.Place(root, symbol, solution, solutionDirectory);

		var builder = new OutlineBuilder();
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}

	/// <summary>
	/// The verbatim declaration cut before its implementation: a method/constructor/delegate through the
	/// closing ')' of its parameter list; a property/indexer keeping a reconstructed accessor list (or cut
	/// before '=>'); a type through the line before its opening brace; a field/event the modifiers, type and
	/// this variable's name. Leading and trailing whitespace is trimmed; an inner multi-line signature is
	/// kept verbatim.
	/// </summary>
	private static string DeclarationHeader(SyntaxNode node, SourceText text)
	{
		string raw = node switch
		{
			VariableDeclaratorSyntax variable => FieldHeader(variable, text),
			BaseMethodDeclarationSyntax method => Slice(node, method.ParameterList.Span.End, text),
			LocalFunctionStatementSyntax local => Slice(node, local.ParameterList.Span.End, text),
			DelegateDeclarationSyntax @delegate => Slice(node, @delegate.ParameterList.Span.End, text),
			PropertyDeclarationSyntax property => AccessorHeader(node, property.ExpressionBody, property.AccessorList, text),
			IndexerDeclarationSyntax indexer => AccessorHeader(node, indexer.ExpressionBody, indexer.AccessorList, text),
			EventDeclarationSyntax @event => Slice(node, @event.Identifier.Span.End, text),
			BaseFieldDeclarationSyntax field => FieldHeader(field.Declaration.Variables[0], text),
			BaseTypeDeclarationSyntax type => Slice(node, type.OpenBraceToken.SpanStart, text),
			_ => node.ToString(),
		};

		return raw.Trim();
	}

	private static string Slice(SyntaxNode node, int endPosition, SourceText text) =>
		text.ToString(TextSpan.FromBounds(node.SpanStart, endPosition));

	private static string AccessorHeader(SyntaxNode node, ArrowExpressionClauseSyntax? expressionBody, AccessorListSyntax? accessorList, SourceText text)
	{
		if (expressionBody is not null)
			return Slice(node, expressionBody.SpanStart, text);

		if (accessorList is not null)
		{
			string prefix = text.ToString(TextSpan.FromBounds(node.SpanStart, accessorList.SpanStart)).TrimEnd();
			IEnumerable<string> accessors = accessorList.Accessors.Select(accessor =>
			{
				string modifiers = accessor.Modifiers.Count > 0 ? accessor.Modifiers.ToString() + " " : "";
				return modifiers + accessor.Keyword.ValueText;
			});
			return $"{prefix} {{ {string.Join("; ", accessors)}; }}";
		}

		return node.ToString();
	}

	private static string FieldHeader(VariableDeclaratorSyntax variable, SourceText text)
	{
		BaseFieldDeclarationSyntax? field = variable.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>();
		return field is null
			? variable.ToString()
			: Slice(field, variable.Identifier.Span.End, text);
	}
}
