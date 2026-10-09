using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Razor;

public static class RazorMapping
{
	public static bool IsRazorGeneratedDocument(SyntaxTree syntaxTree)
	{
		if (syntaxTree?.FilePath is not string path)
			return false;
		return IsRazorGeneratedPath(path);
	}

	public static bool IsRazorGeneratedDocument(Document document)
	{
		if (document?.FilePath is not string path)
			return false;
		return IsRazorGeneratedPath(path);
	}

	public static bool IsRazorGeneratedPath(string filePath)
	{
		if (!filePath.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase)
			&& !filePath.EndsWith("_cshtml.g.cs", StringComparison.OrdinalIgnoreCase))
			return false;

		// Pre-generated files loaded from a prior dotnet build (under the generator's own folder, wherever
		// CompilerGeneratedFilesOutputPath put it), or documents produced by the in-process generator run
		// (RazorDocumentGenerator's RoslynkRazorGenerated folder). Generated paths can mix separators.
		string normalized = filePath.Replace('\\', '/');
		return normalized.Contains("/Microsoft.CodeAnalysis.Razor.Compiler/", StringComparison.OrdinalIgnoreCase)
			|| normalized.Contains("/RoslynkRazorGenerated/", StringComparison.OrdinalIgnoreCase);
	}

	public static FileLinePositionSpan GetDisplaySpan(this Location location)
	{
		FileLinePositionSpan mapped = location.GetMappedLineSpan();
		if (!string.IsNullOrEmpty(mapped.Path))
			return mapped;
		return location.GetLineSpan();
	}

	public static FileLinePositionSpan GetDisplaySpan(this SyntaxTree syntaxTree, TextSpan textSpan)
	{
		FileLinePositionSpan mapped = syntaxTree.GetMappedLineSpan(textSpan);
		if (!string.IsNullOrEmpty(mapped.Path))
			return mapped;
		return syntaxTree.GetLineSpan(textSpan);
	}
}
