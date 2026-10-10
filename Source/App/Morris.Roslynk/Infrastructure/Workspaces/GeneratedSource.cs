using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Outlines;

namespace Morris.Roslynk.Infrastructure.Workspaces;

/// <summary>
/// Recognizes locations declared by a source generator. Such a document has no file on disk (unless the
/// project sets <c>EmitCompilerGeneratedFiles</c>) - its reported path is virtual - so output marks it and
/// callers read it through the semantic tools (get_symbol_body) instead of from disk. Razor's generated
/// <c>.g.cs</c> documents are ordinary model documents, not source-generated ones, and are not marked.
/// </summary>
public static class GeneratedSource
{
	/// <summary>The suffix a marked file node or file field carries in an outline body.</summary>
	public const string FileMarker = ",generated=Y";

	public static bool IsGenerated(Solution solution, SyntaxTree? tree) =>
		tree is not null && solution.GetDocument(tree) is SourceGeneratedDocument;

	/// <summary>
	/// The full type name of the generator that produced <paramref name="tree"/>'s document, or null when the
	/// tree is not generated (or the generator cannot be told). A generated document's path is
	/// <c>.../&lt;assembly&gt;/&lt;generator type&gt;/&lt;hint name&gt;</c>, so the generator is the folder above the hint name.
	/// </summary>
	public static string? GeneratorOf(Solution solution, SyntaxTree? tree)
	{
		if (tree is null || solution.GetDocument(tree) is not SourceGeneratedDocument document || document.FilePath is not string path)
			return null;

		string normalized = path.Replace('\\', '/');
		string hint = document.HintName.Replace('\\', '/');
		if (!normalized.EndsWith("/" + hint, StringComparison.Ordinal))
			return null;

		string folder = normalized[..^(hint.Length + 1)];
		int slash = folder.LastIndexOf('/');
		string generator = slash < 0 ? folder : folder[(slash + 1)..];
		return generator.Length == 0 ? null : generator;
	}

	/// <summary>
	/// Writes the <c>generated=Y</c> (and, when known, <c>generator=</c>) headers for a generated tree; a
	/// regular file writes nothing, so the headers are absent in the common case.
	/// </summary>
	public static void AppendHeaders(OutlineBuilder builder, Solution solution, SyntaxTree? tree)
	{
		if (!IsGenerated(solution, tree))
			return;

		builder.Header("generated", true);
		if (GeneratorOf(solution, tree) is string generator)
			builder.Header("generator", generator);
	}

	/// <summary>Appends <see cref="FileMarker"/> to an outline file path when its tree is generated.</summary>
	public static string MarkPath(string path, Solution solution, SyntaxTree? tree) =>
		IsGenerated(solution, tree) ? path + FileMarker : path;

	/// <summary>
	/// True when nothing hand-written declares <paramref name="symbol"/>: every declaration of its defining
	/// part lives in a source-generated document. Such an edit can only reach references — a generated
	/// document is never written, and the generator re-emits the old declaration, so the edit breaks the
	/// build. A partial member whose definition is hand-written ([GeneratedRegex], [LoggerMessage]) or a
	/// partial type with a hand-written part is not generated-only: the generator follows the hand-written name.
	/// </summary>
	public static bool IsGeneratedOnly(Solution solution, ISymbol symbol) =>
		!DefinitionOf(symbol).DeclaringSyntaxReferences.IsEmpty
		&& DefinitionOf(symbol).DeclaringSyntaxReferences.All(reference => IsGenerated(solution, reference.SyntaxTree));

	/// <summary>The NotSupported message for an edit tool asked to change a generated-only symbol.</summary>
	public static string GeneratedOnlyMessage(Solution solution, ISymbol symbol, string resolvedName, string edit)
	{
		SyntaxTree tree = DefinitionOf(symbol).DeclaringSyntaxReferences[0].SyntaxTree;
		string generator = GeneratorOf(solution, tree) is string name ? $" '{name}'" : "";
		return $"'{resolvedName}' is declared only by source generator{generator}, so it cannot be {edit}: the generated "
			+ "declaration is never written to disk and would be regenerated unchanged. Change the generator or the code that drives it.";
	}

	/// <summary>
	/// The symbol whose declarations name the edit target: a partial member is two symbols in Roslyn and the
	/// definition is the one a generator follows (the generator emits an implementation for the hand-written
	/// declaration), so it decides whether anything hand-written declares the name.
	/// </summary>
	private static ISymbol DefinitionOf(ISymbol symbol) =>
		symbol switch
		{
			IMethodSymbol method => method.PartialDefinitionPart ?? method,
			IPropertySymbol property => property.PartialDefinitionPart ?? property,
			IEventSymbol @event => @event.PartialDefinitionPart ?? @event,
			_ => symbol
		};
}
