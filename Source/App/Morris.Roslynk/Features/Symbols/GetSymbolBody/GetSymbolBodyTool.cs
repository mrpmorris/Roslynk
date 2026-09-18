using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

namespace Morris.Roslynk.Features.Symbols.GetSymbolBody;

[McpServerToolType]
public sealed class GetSymbolBodyTool
{
	public const string GetSymbolBodyName = "get_symbol_body";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;

	public GetSymbolBodyTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = GetSymbolBodyName,
		Title = "Get symbol source text",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Returns the complete verbatim source text of a symbol's declaration - a method, constructor, property,
		indexer, event, field, local function or type - resolved by fully-qualified name. Use this instead of
		reading the file (or grepping it) when you need to see what a member actually does; get_symbol gives
		only the signature, this gives the whole declaration including its body.
		{OutlineDescriptions.CommonMethodInstructions}
		A symbol with one declaration returns '#project=<project>', '#path=<relative/path.cs>',
		and '#loc=<startLine:startCol-endLine:endCol>' headers, a blank line, then the
		source text exactly as it appears in the file - original indentation, line endings and spacing, with no
		reformatting, normalization or truncation. Example:
		  #project=SimpleLibrary
		  #path=SimpleLibrary/Widget.cs
		  #loc=8:2-8:41

		  public int Compute(int value) => value * 2;
		A partial type or partial method declared in several places instead returns a '#parts=<n>' header, then
		one block per part in the body: a 'part=<n>,project=...,path=...,loc=...' line, the
		verbatim text, and a blank line before the next part.
		{OutlineDescriptions.Project}. A name matching several distinct symbols (overloads included) returns
		error=Ambiguous with candidate names; a symbol with no source (a metadata/referenced-assembly symbol,
		a namespace) returns error=NotSupported. {OutlineDescriptions.ErrorBlock}
		""")]
	public async Task<string> GetSymbolBody(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the symbol, e.g. 'MyNamespace.MyType.MyMethod'.")] string symbolName,
		[Description("Include the declaration's leading comments, XML documentation and preceding directives. Default false.")] bool includeLeadingTrivia = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync(cancellationToken);

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve across every projection so a symbol declared only in a branch inactive in the loaded
		// configuration is still found; grouping is signature-aware, so overloads stay separate groups.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution, cancellationToken);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups =
			await ProjectionService.ResolveAsync(SymbolResolver, projections, symbolName, cancellationToken);

		if (groups.Count == 0)
		{
			// Nothing in source. A name that still resolves against a referenced assembly is a real symbol we
			// simply cannot show a body for, which is NotSupported rather than NotFound.
			IReadOnlyList<ISymbol> metadata =
				await SymbolResolver.FindByFullyQualifiedNameWithMetadataAsync(model.Solution, symbolName, cancellationToken);
			if (metadata.Count > 0)
			{
				string assembly = metadata[0].ContainingAssembly is { } owner ? $" It comes from '{owner.Name}'." : "";
				return Failure(Error.NotSupported(
					$"'{symbolName}' has no source declaration, so it has no body to return.{assembly} Use get_symbol for its signature."));
			}

			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(model.Solution, symbolName);
			return Failure(Error.NotFound($"No symbol matched '{symbolName}'.", suggestions.Count > 0 ? suggestions : null));
		}

		if (groups.Count > 1)
		{
			string[] candidates = groups
				.Select(group => SymbolResolver.FullyQualifiedName(group[0].Symbol))
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			return Failure(Error.Ambiguous($"'{symbolName}' matched several symbols (likely overloads).", candidates));
		}

		ProjectionSymbol resolved = groups[0][0];
		ISymbol symbol = resolved.Symbol;

		if (symbol.DeclaringSyntaxReferences.Length == 0)
		{
			string where = symbol.ContainingAssembly is { } assembly ? $" It comes from '{assembly.Name}'." : "";
			return Failure(Error.NotSupported(
				$"'{symbolName}' has no source declaration, so it has no body to return.{where} Use get_symbol for its signature."));
		}

		var parts = new List<Part>();
		foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
		{
			Part? part = await BuildPartAsync(reference, resolved.Projection.Solution, solutionDirectory, includeLeadingTrivia, cancellationToken);
			if (part is not null)
				parts.Add(part);
		}

		if (parts.Count == 0)
			return Failure(Error.NotSupported($"'{symbolName}' has no readable source declaration."));

		return parts.Count == 1 ? Single(parts[0]) : Multiple(parts);
	}

	private static string Single(Part part)
	{
		var builder = new OutlineBuilder();
		if (part.Project is not null)
			builder.Header("project", part.Project);
		builder.Header("path", part.Path);
		builder.Header("loc", part.Location);
		builder.BeginBody();
		builder.Line(0, part.Text);
		return builder.ToString();
	}

	private static string Multiple(IReadOnlyList<Part> parts)
	{
		var builder = new OutlineBuilder();
		builder.Header("parts", parts.Count);
		builder.BeginBody();

		for (int index = 0; index < parts.Count; index++)
		{
			Part part = parts[index];
			if (index > 0)
				builder.Line(0, "");

			string project = part.Project is null ? "" : $"project={OutlineBuilder.Field(part.Project)},";
			builder.Line(0, $"part={index + 1},{project}path={part.Path},loc={part.Location}");
			builder.Line(0, part.Text);
		}

		return builder.ToString();
	}

	private static async Task<Part?> BuildPartAsync(
		SyntaxReference reference,
		Solution solution,
		string? solutionDirectory,
		bool includeLeadingTrivia,
		CancellationToken cancellationToken)
	{
		SyntaxNode node = await reference.GetSyntaxAsync(cancellationToken);

		// A field/event symbol declares a VariableDeclaratorSyntax; the declaration a caller wants to read is
		// the whole field declaration (modifiers, type and initializer) that owns it.
		if (node is VariableDeclaratorSyntax variable && variable.FirstAncestorOrSelf<BaseFieldDeclarationSyntax>() is { } field)
			node = field;

		TextSpan span = includeLeadingTrivia ? WithLeadingTrivia(node) : node.Span;
		SourceText text = await reference.SyntaxTree.GetTextAsync(cancellationToken);
		FileLinePositionSpan display = reference.SyntaxTree.GetDisplaySpan(span);

		string? path = SolutionRelativePath.Of(solutionDirectory, display.Path);
		if (path is null)
			return null;

		return new Part(
			ProjectName.Of(solution, reference.SyntaxTree),
			path,
			$"{display.StartLinePosition.Line + 1}:{display.StartLinePosition.Character + 1}-{display.EndLinePosition.Line + 1}:{display.EndLinePosition.Character + 1}",
			text.ToString(span));
	}

	/// <summary>
	/// The node's span extended back over its leading comments, XML documentation and directives. The blank
	/// lines and indentation that separate the declaration from whatever precedes it are excluded, so the
	/// result starts at the first line that actually belongs to the declaration.
	/// </summary>
	private static TextSpan WithLeadingTrivia(SyntaxNode node)
	{
		SyntaxTriviaList trivia = node.GetLeadingTrivia();
		foreach (SyntaxTrivia candidate in trivia)
		{
			if (candidate.IsKind(SyntaxKind.WhitespaceTrivia)
				|| candidate.IsKind(SyntaxKind.EndOfLineTrivia))
			{
				continue;
			}

			return TextSpan.FromBounds(candidate.FullSpan.Start, node.Span.End);
		}

		return node.Span;
	}

	private sealed record Part(string? Project, string Path, string Location, string Text);
}
