using Morris.Roslynk.Features.CodeActions.ApplyCodeAction;
using Morris.Roslynk.Features.CodeActions.GetCodeActions;
using Morris.Roslynk.Features.Patching.ApplyPatch;
using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Features.Refactorings.ExtractMethod;
using Morris.Roslynk.Features.Signatures.ChangeSignature;
using Morris.Roslynk.Features.Signatures.RenameParameter;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.RazorSupport;

/// <summary>
/// Every tool that takes a document path or edits code works on .razor and .cshtml sources: positions are
/// given in the Razor file, Roslyn runs on the generated C#, and the edits are written to the Razor file.
/// </summary>
public class RazorSourceToolTests
{
	private const string CounterRelativePath = "RazorLib/Counter.razor";
	private const string IndexRelativePath = "CshtmlLib/Views/Home/Index.cshtml";
	private const string IndexTypeName = "AspNetCoreGeneratedDocument.Views_Home_Index";

	// ---- get_code_actions / apply_code_action ----

	[Fact]
	public async Task WhenListingActionsInsideARazorCodeBlock_ThenTheCompilerFixIsOffered()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetCodeActionsTool(registry, TestServices.CodeActions());
		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "unused");

		string result = await subject.GetCodeActions(solutionPath, CounterRelativePath, line, column);

		Assert.DoesNotContain("error=", result);
		Assert.Contains(",Fix,CS0219 ", result);
	}

	[Fact]
	public async Task WhenListingActionsInsideACshtmlFunctionsBlock_ThenTheCompilerFixIsOffered()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetCodeActionsTool(registry, TestServices.CodeActions());
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "unused");

		string result = await subject.GetCodeActions(solutionPath, IndexRelativePath, line, column);

		Assert.DoesNotContain("error=", result);
		Assert.Contains(",Fix,CS0219 ", result);
	}

	[Fact]
	public async Task WhenListingActionsOnRazorMarkup_ThenItIsNotSupported()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new GetCodeActionsTool(registry, TestServices.CodeActions());
		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "<p>Starting");

		string result = await subject.GetCodeActions(solutionPath, CounterRelativePath, line, column);

		Assert.Contains("error=NotSupported", result);
	}

	[Fact]
	public async Task WhenApplyingAnActionDiscoveredInARazorFile_ThenTheRazorFileIsRewrittenOnDisk()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		CodeActionService service = TestServices.CodeActions();
		var getActions = new GetCodeActionsTool(registry, service);
		var subject = new ApplyCodeActionTool(registry, service, new ApplyPipeline());
		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "unused");

		string actions = await getActions.GetCodeActions(solutionPath, CounterRelativePath, line, column);
		string actionId = ActionIdFor(actions, "CS0219");
		string result = await subject.ApplyCodeAction(solutionPath, actionId);

		Assert.Contains("applied=Y", result);
		Assert.Contains(result.Split('\n'), entry => entry.TrimStart('\t') == "Counter.razor");
		string counter = await ReadAsync(solutionPath, CounterRelativePath);
		Assert.DoesNotContain("unused", counter);
		Assert.Contains("CurrentCount++;", counter);
	}

	[Fact]
	public async Task WhenApplyingAnActionDiscoveredInACshtmlFile_ThenTheCshtmlFileIsRewrittenOnDisk()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		CodeActionService service = TestServices.CodeActions();
		var getActions = new GetCodeActionsTool(registry, service);
		var subject = new ApplyCodeActionTool(registry, service, new ApplyPipeline());
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "unused");

		string actions = await getActions.GetCodeActions(solutionPath, IndexRelativePath, line, column);
		string result = await subject.ApplyCodeAction(solutionPath, ActionIdFor(actions, "CS0219"));

		Assert.Contains("applied=Y", result);
		string index = await ReadAsync(solutionPath, IndexRelativePath);
		Assert.DoesNotContain("unused", index);
		Assert.Contains("var builder = new StringBuilder();", index);
	}

	// ---- apply_code_fix ----

	[Fact]
	public async Task WhenFixingACompilerDiagnosticInARazorFile_ThenTheRazorFileIsRewrittenOnDisk()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);

		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "unused");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, CounterRelativePath, "CS0219", line, column);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("unused", await ReadAsync(solutionPath, CounterRelativePath));
	}

	[Fact]
	public async Task WhenFixingACompilerDiagnosticInACshtmlFileWithCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		string before = await ReadAsync(solutionPath, IndexRelativePath);
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "unused");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, IndexRelativePath, "CS0219", line, column, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains(result.Split('\n'), entry => entry.TrimStart('\t') == "Index.cshtml");
		Assert.Equal(before, await ReadAsync(solutionPath, IndexRelativePath));
	}

	[Fact]
	public async Task WhenFixingACompilerDiagnosticInACshtmlFile_ThenTheCshtmlFileIsRewrittenOnDisk()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "unused");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, IndexRelativePath, "CS0219", line, column);

		Assert.Contains("applied=Y", result);
		Assert.DoesNotContain("unused", await ReadAsync(solutionPath, IndexRelativePath));
	}

	[Fact]
	public async Task WhenFixingIDE0005InACshtmlFile_ThenTheUnusedUsingLineAtThePositionIsRemoved()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "System.CodeDom");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, IndexRelativePath, "IDE0005", line, column);

		Assert.Contains("applied=Y", result);
		string index = await ReadAsync(solutionPath, IndexRelativePath);
		Assert.DoesNotContain("@using System.CodeDom", index);
		Assert.Contains("@using System.Text", index);
	}

	[Fact]
	public async Task WhenFixingIDE0005OnAUsedCshtmlUsingLine_ThenItIsNotFoundAndNothingIsWritten()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		string before = await ReadAsync(solutionPath, IndexRelativePath);
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "System.Text");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, IndexRelativePath, "IDE0005", line, column);

		Assert.Contains("error=NotFound", result);
		Assert.Equal(before, await ReadAsync(solutionPath, IndexRelativePath));
	}

	[Fact]
	public async Task WhenFixingARazorDiagnosticAwayFromItsPosition_ThenItIsNotFound()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		(int line, _) = await PositionOfAsync(solutionPath, CounterRelativePath, "@code");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, CounterRelativePath, "CS0219", line, 1);

		Assert.Contains("error=NotFound", result);
	}

	[Fact]
	public async Task WhenFixingADiagnosticAbsentFromTheRazorFile_ThenItIsNotFound()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);

		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "unused");

		string result = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, CounterRelativePath, "CS0168", line, column);

		Assert.Contains("error=NotFound", result);
	}

	// ---- remove_unused_usings ----

	[Fact]
	public async Task WhenRemovingUnusedUsingsFromARazorFile_ThenTheUnusedDirectiveIsRemoved()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);

		string result = await TestServices.RemoveUnusedUsings(registry).RemoveUnusedUsings(solutionPath, CounterRelativePath);

		Assert.Contains("applied=Y", result);
		string counter = await ReadAsync(solutionPath, CounterRelativePath);
		Assert.DoesNotContain("@using System.CodeDom", counter);
		Assert.Contains("@code {", counter);
	}

	[Fact]
	public async Task WhenRemovingUnusedUsingsFromACshtmlFile_ThenOnlyItsOwnUnusedDirectiveIsRemoved()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		string imports = await ReadAsync(solutionPath, "CshtmlLib/Views/_ViewImports.cshtml");

		string result = await TestServices.RemoveUnusedUsings(registry).RemoveUnusedUsings(solutionPath, IndexRelativePath);

		Assert.Contains("applied=Y", result);
		Assert.Contains("removedCount=1", result);
		string index = await ReadAsync(solutionPath, IndexRelativePath);
		Assert.DoesNotContain("@using System.CodeDom", index);
		Assert.StartsWith("@using System.Text\r\n@{", index);
		Assert.Equal(imports, await ReadAsync(solutionPath, "CshtmlLib/Views/_ViewImports.cshtml"));
	}

	[Fact]
	public async Task WhenRemovingUnusedUsingsAcrossTheSolution_ThenRazorFilesAreIncludedAndImportsAreLeftAlone()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		string imports = await ReadAsync(solutionPath, "CshtmlLib/Views/_ViewImports.cshtml");

		string result = await TestServices.RemoveUnusedUsings(registry).RemoveUnusedUsings(solutionPath);

		Assert.Contains("applied=Y", result);
		Assert.Contains(result.Split('\n'), entry => entry.TrimStart('\t') == "Index.cshtml");
		Assert.DoesNotContain("@using System.CodeDom", await ReadAsync(solutionPath, IndexRelativePath));
		Assert.Equal(imports, await ReadAsync(solutionPath, "CshtmlLib/Views/_ViewImports.cshtml"));
	}

	[Fact]
	public async Task WhenRemovingUnusedUsingsFromARazorFileWithCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		string before = await ReadAsync(solutionPath, CounterRelativePath);

		string result = await TestServices.RemoveUnusedUsings(registry).RemoveUnusedUsings(solutionPath, CounterRelativePath, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains(result.Split('\n'), entry => entry.TrimStart('\t') == "Counter.razor");
		Assert.Equal(before, await ReadAsync(solutionPath, CounterRelativePath));
	}

	// ---- extract_method ----

	[Fact]
	public async Task WhenExtractingFromARazorCodeBlock_ThenTheRazorFileIsRewrittenOnDisk()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "CurrentCount++;");

		string result = await subject.ExtractMethod(solutionPath, CounterRelativePath, line, column, line, column + "CurrentCount++;".Length, methodName: "Bump");

		Assert.Contains("applied=Y", result);
		Assert.Contains("method=Bump", result);
		string counter = await ReadAsync(solutionPath, CounterRelativePath);
		// Roslyn formats the new and edited members for the generated class's nesting; they are written back in
		// the file's own tab indentation, and the members it did not touch are left exactly as they were.
		Assert.Contains(NewLines(counter, "\tprotected override void OnInitialized()\n\t{\n\t\tCurrentCount = StartAt;\n\t}\n"), counter);
		Assert.Contains(NewLines(counter, "\tprivate void Bump()\n\t{\n\t\tCurrentCount++;\n\t}\n"), counter);
		Assert.Contains(NewLines(counter, "\tprivate void IncrementCount()\n\t{\n\t\tint unused = 1;\n\t\tBump();\n\t}\n"), counter);
		Assert.DoesNotContain("    ", counter);
	}

	[Fact]
	public async Task WhenExtractingALocalFunctionFromACshtmlFunctionsBlock_ThenTheCshtmlFileIsRewrittenOnDisk()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int startColumn, int endLine, int endColumn) = await GreetingSelectionAsync(solutionPath);

		string result = await subject.ExtractMethod(solutionPath, IndexRelativePath, startLine, startColumn, endLine, endColumn, methodName: "AppendGreeting", asLocalFunction: true);

		Assert.True(result.Contains("applied=Y"), result);
		Assert.Contains("kind=LocalFunction", result);
		Assert.Equal(ExtractedIndex, await ReadAsync(solutionPath, IndexRelativePath));
	}

	[Fact]
	public async Task WhenASecondEditFollowsAFoldedCshtmlEdit_ThenItStillMapsWithoutAReload()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var extract = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int startColumn, int endLine, int endColumn) = await GreetingSelectionAsync(solutionPath);

		string extracted = await extract.ExtractMethod(solutionPath, IndexRelativePath, startLine, startColumn, endLine, endColumn, methodName: "AppendGreeting", asLocalFunction: true);
		(int line, int column) = await PositionOfAsync(solutionPath, IndexRelativePath, "unused");
		string fixedResult = await TestServices.ApplyCodeFix(registry).ApplyCodeFix(solutionPath, IndexRelativePath, "CS0219", line, column);

		Assert.Contains("applied=Y", extracted);
		Assert.True(fixedResult.Contains("applied=Y"), fixedResult);
		Assert.Equal(ExtractedIndex.Replace("\t\tint unused = 1;\r\n", ""), await ReadAsync(solutionPath, IndexRelativePath));
	}

	/// <summary>Index.cshtml after extracting the two Append statements into a local function, in the file's tab indentation.</summary>
	private const string ExtractedIndex =
		"@using System.Text\r\n" +
		"@using System.CodeDom\r\n" +
		"@{\r\n" +
		"\tvar greeting = Format(\"world\");\r\n" +
		"}\r\n" +
		"<p>@greeting</p>\r\n" +
		"<p>@Format(\"again\")</p>\r\n" +
		"\r\n" +
		"@functions {\r\n" +
		"\tprivate string Format(string name)\r\n" +
		"\t{\r\n" +
		"\t\tint unused = 1;\r\n" +
		"\t\tvar builder = new StringBuilder();\r\n" +
		"\t\tAppendGreeting(name, builder);\r\n" +
		"\t\treturn builder.ToString();\r\n" +
		"\r\n" +
		"\t\tstatic void AppendGreeting(string name, StringBuilder builder)\r\n" +
		"\t\t{\r\n" +
		"\t\t\tbuilder.Append(\"Hello \");\r\n" +
		"\t\t\tbuilder.Append(name);\r\n" +
		"\t\t}\r\n" +
		"\t}\r\n" +
		"}\r\n";

	[Fact]
	public async Task WhenRoslynCannotAddAMethodToACshtmlView_ThenTheLocalFunctionAlternativeIsSuggested()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int startLine, int startColumn, int endLine, int endColumn) = await GreetingSelectionAsync(solutionPath);
		string before = await ReadAsync(solutionPath, IndexRelativePath);

		string result = await subject.ExtractMethod(solutionPath, IndexRelativePath, startLine, startColumn, endLine, endColumn);

		Assert.Contains("error=NotSupported", result);
		Assert.Contains("asLocalFunction=true", result);
		Assert.Equal(before, await ReadAsync(solutionPath, IndexRelativePath));
	}

	/// <summary>The two builder.Append statements in Index.cshtml's Format method, end-exclusive.</summary>
	private static async Task<(int StartLine, int StartColumn, int EndLine, int EndColumn)> GreetingSelectionAsync(string solutionPath)
	{
		(int startLine, int startColumn) = await PositionOfAsync(solutionPath, IndexRelativePath, "builder.Append(\"Hello \");");
		(int endLine, int endColumn) = await PositionOfAsync(solutionPath, IndexRelativePath, "builder.Append(name);");
		return (startLine, startColumn, endLine, endColumn + "builder.Append(name);".Length);
	}

	[Fact]
	public async Task WhenExtractingFromARazorFileWithCheckOnly_ThenNothingIsWritten()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "CurrentCount++;");
		string before = await ReadAsync(solutionPath, CounterRelativePath);

		string result = await subject.ExtractMethod(solutionPath, CounterRelativePath, line, column, line, column + "CurrentCount++;".Length, checkOnly: true);

		Assert.Contains("applied=N", result);
		Assert.Contains(result.Split('\n'), entry => entry.TrimStart('\t') == "Counter.razor");
		Assert.Equal(before, await ReadAsync(solutionPath, CounterRelativePath));
	}

	[Fact]
	public async Task WhenExtractingFromRazorMarkup_ThenItIsNotSupported()
	{
		string solutionPath = await CreateRazorSolutionAsync();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ExtractMethodTool(registry, new ApplyPipeline());
		(int line, int column) = await PositionOfAsync(solutionPath, CounterRelativePath, "<p>Starting");

		string result = await subject.ExtractMethod(solutionPath, CounterRelativePath, line, column, line, column + 5);

		Assert.Contains("error=NotSupported", result);
	}

	// ---- change_signature, rename_symbol, rename_parameter, apply_patch on .cshtml ----

	[Fact]
	public async Task WhenChangingTheSignatureOfACshtmlMethod_ThenTheDeclarationAndCallSitesAreRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ChangeSignatureTool(registry, new SymbolResolver(), new ApplyPipeline());

		string result = await subject.ChangeSignature(solutionPath, $"{IndexTypeName}.Format", "int", "times", "1", callSiteArgument: "2");

		Assert.Contains("applied=Y", result);
		Assert.Contains("updatedCallSites=2", result);
		string index = await ReadAsync(solutionPath, IndexRelativePath);
		Assert.Contains("private string Format(string name, int times = 1)", index);
		Assert.Contains("Format(\"world\", times: 2)", index);
		Assert.Contains("@Format(\"again\", times: 2)", index);
	}

	[Fact]
	public async Task WhenRenamingACshtmlMethod_ThenTheDeclarationAndCallSitesAreRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(solutionPath, $"{IndexTypeName}.Format", "Greet");

		Assert.Contains("applied=Y", result);
		string index = await ReadAsync(solutionPath, IndexRelativePath);
		Assert.Contains("private string Greet(string name)", index);
		Assert.Contains("Greet(\"world\")", index);
		Assert.Contains("@Greet(\"again\")", index);
		Assert.DoesNotContain("Format", index);
	}

	[Fact]
	public async Task WhenRenamingACshtmlMethodParameter_ThenTheCshtmlFileIsRewritten()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new RenameParameterTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameParameter(solutionPath, $"{IndexTypeName}.Format", "name", "who");

		Assert.Contains("applied=Y", result);
		string index = await ReadAsync(solutionPath, IndexRelativePath);
		Assert.Contains("private string Format(string who)", index);
		Assert.Contains("builder.Append(who);", index);
	}

	[Fact]
	public async Task WhenPatchingACshtmlFile_ThenItIsApplied()
	{
		string solutionPath = TestSolutions.CreateScratchCshtmlSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(solutionPath);
		var subject = new ApplyPatchTool(registry);
		string patch =
			$"--- a/{IndexRelativePath}\n+++ b/{IndexRelativePath}\n@@ -6,1 +6,1 @@\n-<p>@greeting</p>\n+<p><b>@greeting</b></p>\n";

		string result = await subject.ApplyPatch(solutionPath, patch);

		Assert.Contains("applied=Y", result);
		Assert.Contains("<p><b>@greeting</b></p>", await ReadAsync(solutionPath, IndexRelativePath));
	}

	// ---- helpers ----

	/// <summary>
	/// A scratch Razor solution whose Counter.razor also has an unused @using and an unused local in its
	/// @code block, so fixes, using removal and extraction have something to act on.
	/// </summary>
	private static async Task<string> CreateRazorSolutionAsync()
	{
		string solutionPath = TestSolutions.CreateScratchRazorSolution();
		string counterPath = FullPath(solutionPath, CounterRelativePath);
		string counter = await File.ReadAllTextAsync(counterPath);
		string newline = counter.Contains("\r\n") ? "\r\n" : "\n";
		counter = counter
			.Replace("@using Microsoft.AspNetCore.Components.Web" + newline, "@using Microsoft.AspNetCore.Components.Web" + newline + "@using System.CodeDom" + newline)
			.Replace("\t\tCurrentCount++;", "\t\tint unused = 1;" + newline + "\t\tCurrentCount++;");
		await File.WriteAllTextAsync(counterPath, counter);
		return solutionPath;
	}

	/// <summary><paramref name="text"/> with its "\n" line breaks converted to the line breaks <paramref name="file"/> uses.</summary>
	private static string NewLines(string file, string text) => file.Contains("\r\n") ? text.Replace("\n", "\r\n") : text;

	private static string FullPath(string solutionPath, string relativePath) =>
		Path.Combine(Path.GetDirectoryName(solutionPath)!, relativePath.Replace('/', Path.DirectorySeparatorChar));

	private static Task<string> ReadAsync(string solutionPath, string relativePath) =>
		File.ReadAllTextAsync(FullPath(solutionPath, relativePath));

	/// <summary>The 1-based line and column of the first occurrence of <paramref name="snippet"/> in the file.</summary>
	private static async Task<(int Line, int Column)> PositionOfAsync(string solutionPath, string relativePath, string snippet)
	{
		string[] lines = (await ReadAsync(solutionPath, relativePath)).Split('\n');
		for (int index = 0; index < lines.Length; index++)
		{
			int column = lines[index].IndexOf(snippet, StringComparison.Ordinal);
			if (column >= 0)
				return (index + 1, column + 1);
		}

		throw new InvalidOperationException($"'{snippet}' was not found in '{relativePath}'.");
	}

	private static string ActionIdFor(string actions, string diagnosticId) =>
		actions.Split('\n')
			.Select(line => line.Trim())
			.First(line => line.Contains($",Fix,{diagnosticId} ", StringComparison.Ordinal))
			.Split(',')[0];
}
