using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.GetSymbol;

[McpServerToolType]
public sealed class GetSymbolTool
{
	public const string GetSymbolName = "get_symbol";

	private const string MetadataBucket = "<metadata>";
	private const string GlobalNamespace = "<global>";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public GetSymbolTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
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
		single source match is lean by default: a '#path=<relative/path.cs>' and
		'#loc=<startLine:startCol-endLine:endCol>' header, a blank line, then the verbatim declaration cut
		before its body (the opening brace or '=>'). The declaration line itself conveys accessibility, kind,
		return type, name and parameters, so those are not repeated. Example:
		  #path=VendmanagerWeb/Components/Pages/Ops/TaskManager/TaskManager.razor.cs
		  #loc=196:5-214:6

		  private Task Search(CancellationToken cancellationToken)
		A metadata symbol (no source) instead returns '#kind', '#signature', '#assembly'. An ambiguous name
		returns a '#count' header and a 'file -> namespace -> kind,fully-qualified-name,loc' locator tree to
		disambiguate. Pass format=full for the verbose fields (#fullName, #accessibility, #source)
		and a doc summary when globally-resolvable types or staleness matter.
		{OutlineDescriptions.ErrorBlock} Prefer this over reading the file to identify a symbol.
		""")]
	public async Task<string> GetSymbol(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType' or 'MyNamespace.MyType.MyMethod'.")] string symbolName,
		[Description("Output detail: 'lean' (default) is path + loc + the verbatim declaration; 'full' adds the fully-qualified headline fields and doc summary.")] string format = "lean",
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, symbolName);

		if (matches.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(model.Solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}

		if (matches.Count > 1)
			return Locator(matches, model, solutionDirectory);

		bool full = string.Equals(format, "full", StringComparison.OrdinalIgnoreCase);
		return full
			? DetailFull(matches[0], model, solutionDirectory)
			: await DetailLeanAsync(matches[0], solutionDirectory, cancellationToken);
	}

	private async Task<string> DetailLeanAsync(ISymbol symbol, string? solutionDirectory, CancellationToken cancellationToken)
	{
		SyntaxReference? reference = symbol.DeclaringSyntaxReferences.FirstOrDefault();
		if (reference is null)
			return MetadataLean(symbol);

		FileLinePositionSpan span = reference.SyntaxTree.GetLineSpan(reference.Span);
		SourceText text = await reference.SyntaxTree.GetTextAsync(cancellationToken);
		SyntaxNode node = await reference.GetSyntaxAsync(cancellationToken);

		var builder = new OutlineBuilder();
		builder.Header("path", SolutionRelativePath.Of(solutionDirectory, span.Path)!);
		builder.Header("loc", $"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}");
		builder.BeginBody();
		builder.Line(0, DeclarationHeader(node, text));
		return builder.ToString();
	}

	private string MetadataLean(ISymbol symbol)
	{
		var builder = new OutlineBuilder();
		builder.Header("kind", SymbolKindText.Of(symbol));
		builder.Header("signature", symbol.ToDisplayString());
		if (symbol.ContainingAssembly is { } assembly)
			builder.Header("assembly", assembly.Name);

		return builder.ToString();
	}

	private string DetailFull(ISymbol symbol, SolutionModel model, string? solutionDirectory)
	{
		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);

		var builder = new OutlineBuilder();
		builder.Header("fullName", SymbolResolver.FullyQualifiedName(symbol));
		builder.Header("kind", SymbolKindText.Of(symbol));
		builder.Header("accessibility", symbol.DeclaredAccessibility.ToString().ToLowerInvariant());
		builder.Header("signature", symbol.ToDisplayString());
		builder.Header("source", location is null ? "metadata" : "source");

		if (location is null)
		{
			if (symbol.ContainingAssembly is { } assembly)
				builder.Header("assembly", assembly.Name);
		}
		else
		{
			FileLinePositionSpan span = location.GetLineSpan();
			string path = SolutionRelativePath.Of(solutionDirectory, span.Path)!;
			builder.Header("location", $"{path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}");
		}

		builder.Status(model.Status);

		SymbolDocumentation documentation = DocumentationReader.Read(symbol);
		if (documentation.Summary is not null || documentation.InheritedFrom is not null)
		{
			builder.BeginBody();
			if (documentation.Summary is { } summary)
				builder.Line(0, "summary: " + OutlineBuilder.Sanitize(summary));

			if (documentation.InheritedFrom is { } inherited)
			{
				string where = inherited.SourcePath is { } source
					? " " + SolutionRelativePath.Of(solutionDirectory, source)
					: inherited.Assembly is { } assembly
						? " " + assembly
						: "";
				builder.Line(0, $"inheritedFrom: {inherited.Symbol}{where}");
			}
		}

		return builder.ToString();
	}

	private string Locator(IReadOnlyList<ISymbol> matches, SolutionModel model, string? solutionDirectory)
	{
		var root = new SymbolNode();
		foreach (ISymbol symbol in matches)
		{
			Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
			string file = location?.SourceTree?.FilePath is string path
				? SolutionRelativePath.Of(solutionDirectory, path)!
				: symbol.ContainingAssembly?.Name ?? MetadataBucket;

			INamespaceSymbol? containing = symbol.ContainingNamespace;
			string @namespace = containing is null || containing.IsGlobalNamespace ? GlobalNamespace : containing.ToDisplayString();

			SymbolNode leaf = root.Child(file).Child(@namespace).Child($"{SymbolKindText.Of(symbol)},{SymbolResolver.FullyQualifiedName(symbol)}");
			if (location is not null)
			{
				FileLinePositionSpan span = location.GetLineSpan();
				leaf.AddLocation(
					span.StartLinePosition.Line + 1,
					span.StartLinePosition.Character + 1,
					span.EndLinePosition.Line + 1,
					span.EndLinePosition.Character + 1);
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("count", matches.Count);
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
