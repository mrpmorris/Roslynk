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

		// Pre-generated files loaded from a prior dotnet build, or documents produced by the in-process
		// generator run (RazorDocumentGenerator's obj/RoslynkRazorGenerated folder).
		return filePath.Contains("generated" + System.IO.Path.DirectorySeparatorChar + "Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.OrdinalIgnoreCase)
			|| filePath.Contains("generated/Microsoft.CodeAnalysis.Razor.Compiler", StringComparison.OrdinalIgnoreCase)
			|| filePath.Contains("obj" + System.IO.Path.DirectorySeparatorChar + "RoslynkRazorGenerated", StringComparison.OrdinalIgnoreCase)
			|| filePath.Contains("obj/RoslynkRazorGenerated", StringComparison.OrdinalIgnoreCase);
	}

	public static FileLinePositionSpan GetDisplaySpan(this Location location)
	{
		FileLinePositionSpan mapped = location.GetMappedLineSpan();
		if (mapped.Path.Length > 0)
			return mapped;
		return location.GetLineSpan();
	}

	public static FileLinePositionSpan GetDisplaySpan(this SyntaxTree syntaxTree, TextSpan textSpan)
	{
		FileLinePositionSpan mapped = syntaxTree.GetMappedLineSpan(textSpan);
		if (mapped.Path.Length > 0)
			return mapped;
		return syntaxTree.GetLineSpan(textSpan);
	}
}
