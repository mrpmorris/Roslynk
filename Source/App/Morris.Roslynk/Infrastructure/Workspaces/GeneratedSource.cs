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
}
