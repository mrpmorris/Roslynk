using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.Refactorings.ExtractMethod;

[McpServerToolType]
public sealed class ExtractMethodTool
{
	public const string ExtractMethodName = "extract_method";

	/// <summary>The equivalence keys Roslyn's Extract Method provider gives its two actions.</summary>
	private const string ExtractMethodKey = "Extract_method";
	private const string ExtractLocalFunctionKey = "Extract_local_function";

	private const string ProviderTypeName = "ExtractMethodCodeRefactoringProvider";
	private const int MaxReportedErrors = 5;

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ApplyPipeline ApplyPipeline;

	public ExtractMethodTool(InstanceRegistry instanceRegistry, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[Description("""
		Extracts contiguous C# statements or one expression into a method/local function, replacing the
		selection with a call. Roslyn data-flow determines parameters, ref/out, return value, static/async
		and call-site code. Surrounding whitespace is ignored.

		Additional headers: method (final name), symbolName (usable in name-based tools, e.g.
		N.T.M(int).NewMethod(string) for a local function), kind (Method|LocalFunction), signature
		(declaration header), call (statement containing the call).

		Errors:
		- NotSupported: extraction unavailable, behavior-change warnings, new compile errors (listed),
		  or generated .g.cs targets (including Razor output).
		- Conflict: methodName would bind a call to another member.
		- NotFound: unloaded source file.
		- Invalid: empty/out-of-range selection or invalid methodName.

		Razor selections outside C# return NotSupported; inserting a method outside @code may be unmappable.
		""")]
	[McpServerTool(
		Name = ExtractMethodName,
		Title = "Extract a selection into a method",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	public async Task<string> ExtractMethod(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.FilePath)] string filePath,
		[Description("Selection start line.")] int line,
		[Description("Selection start column.")] int column,
		[Description("Selection end line.")] int endLine,
		[Description("Selection end column.")] int endColumn,
		[Description("Extracted method name; omit to keep Roslyn's chosen name.")] string? methodName = null,
		[Description("Extract as a local function.")] bool asLocalFunction = false,
		[Description("Preview code and changed paths without writing.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (methodName is not null && !IsValidMethodName(methodName))
			return Failure(Error.Invalid($"'{methodName}' is not a valid C# method name."));

		if (model.Solution is null)
			return Failure(Error.Indexing(instance.LoadedProjects));

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(solution, filePath, cancellationToken);
		if (source is null)
			return Failure(Error.NotFound($"'{filePath}' is not a solution-compiled .cs, .razor or .cshtml document."));
		Document document = source.Document;
		if (!source.IsRazor && document.FilePath?.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) == true)
			return Failure(Error.NotSupported("Extracting from generated code (.g.cs, including Razor output) is not supported; pass the .razor/.cshtml source it was generated from."));
		if (document.Project.Language != LanguageNames.CSharp)
			return Failure(Error.NotSupported("Only C# documents are supported."));

		SourceText text = await source.GetSourceTextAsync(cancellationToken);
		if (!TryGetSelection(text, line, column, endLine, endColumn, out TextSpan sourceSpan, out string? invalid))
			return Failure(Error.Invalid(invalid!));
		if (await source.MapToDocumentAsync(sourceSpan, cancellationToken) is not TextSpan span)
			return Failure(Error.NotSupported($"The selection {line}:{column}-{endLine}:{endColumn} is not inside C# code (an @code block or expression) in '{filePath}'."));

		CodeAction? action = await FindActionAsync(document, span, asLocalFunction ? ExtractLocalFunctionKey : ExtractMethodKey, cancellationToken);
		if (action is null)
		{
			// Roslyn will not add a member where generated code hides the insertion point (some Razor/MVC
			// views), yet a local function still fits inside the containing member.
			if (!asLocalFunction && await FindActionAsync(document, span, ExtractLocalFunctionKey, cancellationToken) is not null)
			{
				return Failure(Error.NotSupported(
					"Roslyn cannot add a new method here (the generated code around the containing type is hidden, as in " +
					"some .cshtml views); the selection can be extracted as a local function: retry with asLocalFunction=true."));
			}

			return Failure(Error.NotSupported(
				"Roslyn cannot extract this selection. Select one complete expression, or one or more complete statements " +
				"from the same block, that contain no jump (return/break/continue/goto) out of the selection unless the " +
				"selection ends the block, and no yield."));
		}

		Solution? extracted = await CodeActionService.ChangedSolutionAsync(action, cancellationToken);
		if (extracted is null)
			return Failure(Error.NotSupported("Roslyn's Extract Method produced no changes for this selection."));

		Document extractedDocument = extracted.GetDocument(document.Id)!;
		SyntaxNode extractedRoot = (await extractedDocument.GetSyntaxRootAsync(cancellationToken))!;

		string[] warnings = extractedRoot.GetAnnotatedNodesAndTokens(WarningAnnotation.Kind)
			.SelectMany(item => item.GetAnnotations(WarningAnnotation.Kind))
			.Select(annotation => WarningAnnotation.GetDescription(annotation) ?? "")
			.Where(description => description.Length > 0)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		if (warnings.Length > 0)
			return Failure(Error.NotSupported($"Roslyn flagged that extracting this selection may change behavior: {string.Join(" ", warnings)}"));

		SyntaxNode originalRoot = (await document.GetSyntaxRootAsync(cancellationToken))!;
		SyntaxNode? declaration = FindNewDeclaration(originalRoot, extractedRoot, name: null);
		if (declaration is null)
			return Failure(Error.NotSupported("Roslyn's extraction could not be located in the result, so it was not applied."));

		if (methodName is not null && !string.Equals(methodName, NameOf(declaration), StringComparison.Ordinal))
		{
			SemanticModel defaultModel = (await extractedDocument.GetSemanticModelAsync(cancellationToken))!;
			ISymbol? defaultSymbol = defaultModel.GetDeclaredSymbol(declaration, cancellationToken);
			if (defaultSymbol is null)
				return Failure(Error.NotSupported("Roslyn's extracted method could not be resolved, so it was not renamed or applied."));

			extracted = await Renamer.RenameSymbolAsync(extracted, defaultSymbol, new SymbolRenameOptions(), methodName, cancellationToken);
			extractedDocument = extracted.GetDocument(document.Id)!;
			extractedRoot = (await extractedDocument.GetSyntaxRootAsync(cancellationToken))!;
			declaration = FindNewDeclaration(originalRoot, extractedRoot, methodName);
			if (declaration is null)
				return Failure(Error.Conflict($"Renaming the extracted method to '{methodName}' did not produce a declaration of that name; choose another methodName."));
		}

		string finalName = NameOf(declaration);
		SemanticModel semanticModel = (await extractedDocument.GetSemanticModelAsync(cancellationToken))!;
		ISymbol? declared = semanticModel.GetDeclaredSymbol(declaration, cancellationToken);
		SimpleNameSyntax[] calls = extractedRoot.DescendantNodes()
			.OfType<SimpleNameSyntax>()
			.Where(node => string.Equals(node.Identifier.ValueText, finalName, StringComparison.Ordinal))
			.Where(node => declared is not null && SymbolEqualityComparer.Default.Equals(
				semanticModel.GetSymbolInfo(node, cancellationToken).Symbol?.OriginalDefinition, declared.OriginalDefinition))
			.ToArray();
		if (calls.Length == 0)
			return Failure(Error.Conflict($"No call binds to the extracted '{finalName}'; the name clashes with another member. Choose another methodName."));

		IReadOnlyList<string> newErrors = await NewErrorsAsync(solution, extracted, cancellationToken);
		if (newErrors.Count > 0)
		{
			string listed = string.Join("; ", newErrors.Take(MaxReportedErrors));
			string more = newErrors.Count > MaxReportedErrors ? $" (and {newErrors.Count - MaxReportedErrors} more)" : "";
			return Failure(Error.NotSupported($"Extracting this selection would introduce compile errors, so nothing was written: {listed}{more}"));
		}

		string signature = Signature(declaration);
		string call = CallSite(calls[0]);

		try
		{
			extracted = await RazorGeneratedChangeFolder.FoldAsync(solution, extracted, cancellationToken);
		}
		catch (RazorMappingException exception)
		{
			return Failure(RazorGeneratedChangeFolder.ErrorFor(exception));
		}

		IReadOnlyList<string> files;
		if (checkOnly)
		{
			files = ApplyPipeline.GetChangedFilePaths(solution, extracted);
		}
		else
		{
			try
			{
				files = await ApplyPipeline.ApplyAsync(instance, extracted, basedOn: solution, cancellationToken);
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
		builder.Header("method", finalName);
		if (declared is not null)
			builder.Header("symbolName", SymbolResolver.SignatureName(declared));
		builder.Header("kind", declaration is LocalFunctionStatementSyntax ? "LocalFunction" : "Method");
		builder.Header("signature", signature);
		builder.Header("call", call);
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, files, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}

	private static bool IsValidMethodName(string name) =>
		SyntaxFacts.IsValidIdentifier(name) && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None;

	private static string NameOf(SyntaxNode declaration) =>
		declaration switch
		{
			MethodDeclarationSyntax method => method.Identifier.ValueText,
			LocalFunctionStatementSyntax localFunction => localFunction.Identifier.ValueText,
			_ => ""
		};

	private static IEnumerable<SyntaxNode> Declarations(SyntaxNode root) =>
		root.DescendantNodes().Where(node => node is MethodDeclarationSyntax or LocalFunctionStatementSyntax);

	/// <summary>
	/// The method or local function declared in <paramref name="updatedRoot"/> that <paramref name="originalRoot"/>
	/// did not have: the first declaration of a name that now occurs more often than it did before. Roslyn
	/// does not leave a marker on what it generated, so the extraction is identified by this difference.
	/// </summary>
	private static SyntaxNode? FindNewDeclaration(SyntaxNode originalRoot, SyntaxNode updatedRoot, string? name)
	{
		Dictionary<string, int> before = Declarations(originalRoot)
			.GroupBy(NameOf, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

		var seen = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (SyntaxNode declaration in Declarations(updatedRoot))
		{
			string declared = NameOf(declaration);
			seen[declared] = seen.GetValueOrDefault(declared) + 1;
			if (name is not null && !string.Equals(declared, name, StringComparison.Ordinal))
				continue;
			if (seen[declared] > before.GetValueOrDefault(declared))
				return declaration;
		}

		return null;
	}

	/// <summary>
	/// Converts the 1-based, end-exclusive selection to a span, then trims surrounding whitespace so a
	/// selection of whole lines (including their indentation and line break) is treated as the code on them.
	/// </summary>
	private static bool TryGetSelection(SourceText text, int line, int column, int endLine, int endColumn, out TextSpan span, out string? invalid)
	{
		span = default;
		invalid = null;

		if (!TryGetOffset(text, line, column, out int start) || !TryGetOffset(text, endLine, endColumn, out int end))
		{
			invalid = $"The selection {line}:{column}-{endLine}:{endColumn} is outside the document ({text.Lines.Count} lines).";
			return false;
		}

		if (end < start)
		{
			invalid = "The selection ends before it starts.";
			return false;
		}

		while (start < end && char.IsWhiteSpace(text[start]))
			start++;
		while (end > start && char.IsWhiteSpace(text[end - 1]))
			end--;

		if (start == end)
		{
			invalid = "The selection is empty; select the statements or expression to extract.";
			return false;
		}

		span = TextSpan.FromBounds(start, end);
		return true;
	}

	private static bool TryGetOffset(SourceText text, int line, int column, out int offset)
	{
		offset = 0;
		if (line < 1 || line > text.Lines.Count || column < 1)
			return false;

		TextLine textLine = text.Lines[line - 1];
		if (column - 1 > textLine.SpanIncludingLineBreak.Length)
			return false;

		offset = textLine.Start + column - 1;
		return true;
	}

	private static async Task<CodeAction?> FindActionAsync(Document document, TextSpan span, string equivalenceKey, CancellationToken cancellationToken)
	{
		CodeRefactoringProvider? provider = CodeActionCatalog.Instance.RefactoringProviders
			.FirstOrDefault(candidate => candidate.GetType().Name == ProviderTypeName);
		if (provider is null)
			return null;

		var registered = new List<CodeAction>();
		var context = new CodeRefactoringContext(document, span, registered.Add, cancellationToken);
		await provider.ComputeRefactoringsAsync(context);

		return registered
			.SelectMany(action => action.NestedActions.IsDefaultOrEmpty ? [action] : action.NestedActions.AsEnumerable())
			.FirstOrDefault(action => string.Equals(action.EquivalenceKey, equivalenceKey, StringComparison.Ordinal));
	}

	/// <summary>
	/// Compiler errors in the changed documents of <paramref name="updated"/> that were not already present
	/// (by id and message) in the same documents of <paramref name="original"/>.
	/// </summary>
	private static async Task<IReadOnlyList<string>> NewErrorsAsync(Solution original, Solution updated, CancellationToken cancellationToken)
	{
		var newErrors = new List<string>();
		foreach (ProjectChanges projectChanges in updated.GetChanges(original).GetProjectChanges())
		{
			Compilation? before = await original.GetProject(projectChanges.ProjectId)!.GetCompilationAsync(cancellationToken);
			Compilation? after = await updated.GetProject(projectChanges.ProjectId)!.GetCompilationAsync(cancellationToken);
			if (before is null || after is null)
				continue;

			foreach (DocumentId documentId in projectChanges.GetChangedDocuments())
			{
				SyntaxTree? beforeTree = await original.GetDocument(documentId)!.GetSyntaxTreeAsync(cancellationToken);
				SyntaxTree? afterTree = await updated.GetDocument(documentId)!.GetSyntaxTreeAsync(cancellationToken);
				if (beforeTree is null || afterTree is null)
					continue;

				var existing = before.GetSemanticModel(beforeTree).GetDiagnostics(cancellationToken: cancellationToken)
					.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
					.Select(ErrorKey)
					.GroupBy(key => key)
					.ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

				foreach (Diagnostic diagnostic in after.GetSemanticModel(afterTree).GetDiagnostics(cancellationToken: cancellationToken)
					.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
				{
					string key = ErrorKey(diagnostic);
					if (existing.TryGetValue(key, out int count) && count > 0)
					{
						existing[key] = count - 1;
						continue;
					}

					int line = diagnostic.Location.GetDisplaySpan().StartLinePosition.Line + 1;
					newErrors.Add($"{key} (line {line})");
				}
			}
		}

		return newErrors;
	}

	private static string ErrorKey(Diagnostic diagnostic) => $"{diagnostic.Id}: {diagnostic.GetMessage()}";

	/// <summary>The declaration header (modifiers, return type, name, parameters, constraints) on one line.</summary>
	private static string Signature(SyntaxNode declaration)
	{
		SyntaxNode? end = declaration switch
		{
			MethodDeclarationSyntax method => (SyntaxNode?)method.ConstraintClauses.LastOrDefault() ?? method.ParameterList,
			LocalFunctionStatementSyntax localFunction => (SyntaxNode?)localFunction.ConstraintClauses.LastOrDefault() ?? localFunction.ParameterList,
			_ => null
		};

		SourceText text = declaration.SyntaxTree.GetText();
		string header = end is null
			? declaration.ToString()
			: text.ToString(TextSpan.FromBounds(declaration.SpanStart, end.Span.End));
		return CollapseWhitespace(header);
	}

	/// <summary>The statement (or, for an expression-bodied member, the expression) that now holds the call.</summary>
	private static string CallSite(SyntaxNode call)
	{
		SyntaxNode node = (SyntaxNode?)call.FirstAncestorOrSelf<StatementSyntax>()
			?? (SyntaxNode?)call.FirstAncestorOrSelf<ArrowExpressionClauseSyntax>()
			?? call;
		return CollapseWhitespace(node.ToString());
	}

	private static string CollapseWhitespace(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}
