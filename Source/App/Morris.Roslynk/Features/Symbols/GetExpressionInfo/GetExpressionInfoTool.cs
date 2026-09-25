using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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

namespace Morris.Roslynk.Features.Symbols.GetExpressionInfo;

[McpServerToolType]
public sealed partial class GetExpressionInfoTool
{
	public const string GetExpressionInfoName = "get_expression_info";

	/// <summary>Longest expression text echoed back; longer text is cut and marked with an ellipsis.</summary>
	internal const int MaxExpressionTextLength = 200;

	/// <summary>Longest documentation summary echoed back.</summary>
	internal const int MaxDocumentationLength = 400;

	/// <summary>The value reported for a fact the compiler does not supply, so an absent fact is never guessed.</summary>
	internal const string Unavailable = "none";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ProjectionService ProjectionService;

	public GetExpressionInfoTool(InstanceRegistry instanceRegistry, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = GetExpressionInfoName,
		Title = "Get compiler facts about an expression",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Returns what the compiler says about the expression at a source position (file, 1-based line and
		column): its type, the symbol it binds to (with the overload chosen), nullability, constant value and
		implicit conversion. Use it before editing code whose meaning is not obvious from the text (var,
		overloads, implicit conversions, target-typed new, nullable flow). A position on a member name or a
		called method reports the whole access or invocation, so an invocation's type is its return type and
		its symbol the selected overload.
		{OutlineDescriptions.ProjectionCoverage}
		{OutlineDescriptions.CommonMethodInstructions}
		The result is header only: 'expr' (the expression text, cut at 200 characters with '...'),
		'exprKind' (the Roslyn syntax kind), 'path' and 'loc' of the expression;
		'type' and 'convertedType' (fully qualified, with '?' for a nullable annotation), 'nullability' as
		<annotation>/<flowState> (e.g. NotAnnotated/NotNull, Annotated/MaybeNull; None/None when nullable
		analysis is disabled); 'conversion' (e.g. 'implicit numeric', 'implicit user-defined <method>', or
		'identity'); 'constant' (a C# literal, or 'null'); 'symbol' and 'symbolKind' for the bound symbol,
		whose name can be sent straight to the name-based tools for a type or member (a local, parameter or
		range variable reports its bare name); 'instantiation' when the symbol is a constructed generic or
		reduced extension method; 'origin' (source|metadata) with 'symbolPath'/'symbolLoc' for source or
		'assembly' for metadata, and 'doc' (the XML summary, when available). A fact the compiler does not
		supply is reported as '{Unavailable}' rather than guessed: 'type={Unavailable}' for a method group or
		namespace, 'constant={Unavailable}' for a non-constant, 'symbol={Unavailable}' for an expression that
		binds to no symbol, in which case 'candidateReason' (e.g. OverloadResolutionFailure) and a
		pipe-delimited 'candidates' list appear when the compiler has candidates. A position on the name a
		declaration introduces (a local, parameter, field, property, method, type, foreach or pattern variable)
		reports the declared symbol instead, like an editor hover: 'declaration=Y', 'expr' is the name,
		'exprKind' the declaring syntax kind, 'type' the declared type (a method's return type), and
		'nullability' the declared annotation with the initializer's flow state (None without one). A position
		on neither (whitespace, a comment, a keyword, punctuation) is error=NotFound; a file not in the
		solution is error=NotFound. Works in .razor/.cshtml files, with positions in the Razor source.
		{OutlineDescriptions.ErrorBlock}
		""")]
	public async Task<string> GetExpressionInfo(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path to the .cs, .razor or .cshtml file; absolute, or relative to the solution folder.")] string filePath,
		[Description("1-based line of a position inside the expression.")] int line,
		[Description("1-based column of a position inside the expression.")] int column)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetExpressionInfoCoreAsync(model, instance, filePath, line, column, CancellationToken.None);
	}

	internal async Task<string> GetExpressionInfoCoreAsync(SolutionModel model, RoslynInstance instance, string filePath, int line, int column, CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		RazorSourceDocument? baseDocument = await RazorSourceDocument.ResolveAsync(model.Solution, filePath, token);
		if (baseDocument is null)
			return Failure(Error.NotFound($"'{filePath}' is not a C#, Razor or CSHTML document compiled in the solution."));

		SourceText sourceText = await baseDocument.GetSourceTextAsync(token);
		if (line < 1 || line > sourceText.Lines.Count)
			return Failure(Error.NotFound($"Line {line} is outside '{filePath}', which has {sourceText.Lines.Count} lines."));

		TextLine textLine = sourceText.Lines[line - 1];
		if (column < 1 || column - 1 > textLine.End - textLine.Start)
			return Failure(Error.NotFound($"Column {column} is outside line {line} of '{filePath}'."));

		int sourcePosition = textLine.Start + column - 1;

		// Try each projection in turn: code in a branch inactive in the loaded configuration is disabled trivia
		// in the base projection and only becomes an expression where that branch is active.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution, token);
		foreach (Projection projection in projections)
		{
			RazorSourceDocument? source = ReferenceEquals(projection.Solution, model.Solution)
				? baseDocument
				: await RazorSourceDocument.ResolveAsync(projection.Solution, filePath, token);
			if (source is null)
				continue;

			TextSpan? mapped = await source.MapToDocumentAsync(new TextSpan(sourcePosition, 0), token);
			if (mapped is null)
				continue;

			SyntaxNode? root = await source.Document.GetSyntaxRootAsync(token);
			SemanticModel? semanticModel = await source.Document.GetSemanticModelAsync(token);
			if (root is null || semanticModel is null)
				continue;

			ExpressionSyntax? expression = FindExpression(root, mapped.Value.Start);
			if (expression is not null)
				return Describe(model.Solution, semanticModel, expression, token);

			if (FindDeclaredName(root, semanticModel, mapped.Value.Start, token) is (SyntaxToken name, ISymbol declared))
				return DescribeDeclaration(model.Solution, semanticModel, name, declared, token);
		}

		return Failure(Error.NotFound(
			$"No expression or declared name at {filePath} ({line}, {column}). Place the position on an identifier, "
			+ "literal, member access, invocation or other expression, or on the name a declaration introduces; "
			+ "keywords, punctuation, comments and whitespace are neither."));
	}

	/// <summary>
	/// The expression at <paramref name="position"/>, widened to the construct an editor means: a member name
	/// reports its whole member access, a called name its invocation, and a created type its object creation.
	/// A declaration's own name is not an expression, so it returns null (see <see cref="FindDeclaredName"/>).
	/// </summary>
	internal static ExpressionSyntax? FindExpression(SyntaxNode root, int position)
	{
		if (TokenAt(root, position) is not SyntaxToken token)
			return null;

		// The nearest enclosing expression, unless a statement or declaration comes first: a keyword or a
		// declared name inside a lambda body must not report the lambda.
		ExpressionSyntax? expression = null;
		for (SyntaxNode? node = token.Parent; node is not null; node = node.Parent)
		{
			if (node is ExpressionSyntax found)
			{
				expression = found;
				break;
			}

			if (node is StatementSyntax or MemberDeclarationSyntax or AccessorDeclarationSyntax or VariableDeclaratorSyntax
				or ParameterSyntax or ParameterListSyntax or TypeParameterSyntax or CatchDeclarationSyntax)
			{
				return null;
			}
		}

		if (expression is null)
			return null;

		while (true)
		{
			SyntaxNode? parent = expression.Parent;
			ExpressionSyntax? widened = parent switch
			{
				MemberAccessExpressionSyntax access when access.Name == expression => access,
				MemberBindingExpressionSyntax binding when binding.Name == expression => binding,
				QualifiedNameSyntax qualified when qualified.Right == expression => qualified,
				AliasQualifiedNameSyntax alias when alias.Name == expression => alias,
				InvocationExpressionSyntax invocation when invocation.Expression == expression => invocation,
				ObjectCreationExpressionSyntax creation when creation.Type == expression => creation,
				_ => null
			};
			if (widened is null)
				return expression;

			expression = widened;
		}
	}

	/// <summary>The token under <paramref name="position"/>; a position directly after a token (a cursor at the end of an identifier) still means that token.</summary>
	private static SyntaxToken? TokenAt(SyntaxNode root, int position)
	{
		SyntaxToken token = root.FindToken(position);
		if (token.Span.Contains(position))
			return token;
		if (position == 0)
			return null;

		token = root.FindToken(position - 1);
		return token.Span.End == position ? token : null;
	}

	/// <summary>
	/// The name a declaration introduces at <paramref name="position"/> (a local, parameter, field, property,
	/// method, type, foreach or pattern variable...) and the symbol it declares; the counterpart of an editor's
	/// hover on a declared name, which is not itself an expression.
	/// </summary>
	internal static (SyntaxToken Name, ISymbol Symbol)? FindDeclaredName(SyntaxNode root, SemanticModel semanticModel, int position, CancellationToken token)
	{
		if (TokenAt(root, position) is not SyntaxToken name || !name.IsKind(SyntaxKind.IdentifierToken) || name.Parent is not SyntaxNode declaration)
			return null;

		ISymbol? symbol = semanticModel.GetDeclaredSymbol(declaration, token);
		return symbol is not null && symbol.Name == name.ValueText ? (name, symbol) : null;
	}

	private static string DescribeDeclaration(Solution baseSolution, SemanticModel semanticModel, SyntaxToken name, ISymbol symbol, CancellationToken token)
	{
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(baseSolution);
		var builder = new OutlineBuilder();

		builder.Header("expr", name.ValueText);
		builder.Header("exprKind", name.Parent!.Kind().ToString());
		builder.Header("declaration", true);
		AppendLocation(builder, solutionDirectory, name.SyntaxTree!, name.Span);

		(ITypeSymbol? type, NullableAnnotation annotation) = symbol switch
		{
			ILocalSymbol local => (local.Type, local.NullableAnnotation),
			IParameterSymbol parameter => (parameter.Type, parameter.NullableAnnotation),
			IFieldSymbol field => (field.Type, field.NullableAnnotation),
			IPropertySymbol property => (property.Type, property.NullableAnnotation),
			IEventSymbol @event => (@event.Type, @event.NullableAnnotation),
			IMethodSymbol method => (method.ReturnType, method.ReturnNullableAnnotation),
			IRangeVariableSymbol => (null, NullableAnnotation.None),
			_ => ((ITypeSymbol?)null, NullableAnnotation.None)
		};
		builder.Header("type", TypeText(type, annotation));

		// A declaration's flow state is that of the value it starts with, when it has one.
		NullableFlowState flowState = name.Parent is VariableDeclaratorSyntax { Initializer.Value: { } initializer }
			? semanticModel.GetTypeInfo(initializer, token).Nullability.FlowState
			: NullableFlowState.None;
		builder.Header("nullability", $"{annotation}/{flowState}");

		object? constantValue = symbol switch
		{
			ILocalSymbol { HasConstantValue: true } local => local.ConstantValue,
			IFieldSymbol { HasConstantValue: true } field => field.ConstantValue,
			_ => Unavailable
		};
		builder.Header("constant", ReferenceEquals(constantValue, Unavailable) ? Unavailable : ConstantText(constantValue));

		AppendSymbol(builder, solutionDirectory, symbol, token);
		return builder.ToString();
	}

	private static void AppendLocation(OutlineBuilder builder, string? solutionDirectory, SyntaxTree tree, TextSpan textSpan)
	{
		FileLinePositionSpan span = tree.GetDisplaySpan(textSpan);
		builder.Header("path", SolutionRelativePath.Of(solutionDirectory, span.Path));
		builder.Header("loc", $"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}");
	}

	private static string Describe(Solution baseSolution, SemanticModel semanticModel, ExpressionSyntax expression, CancellationToken token)
	{
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(baseSolution);
		var builder = new OutlineBuilder();

		builder.Header("expr", Truncate(CollapseWhitespace(expression.ToString()), MaxExpressionTextLength));
		builder.Header("exprKind", expression.Kind().ToString());

		AppendLocation(builder, solutionDirectory, expression.SyntaxTree, expression.Span);

		TypeInfo typeInfo = semanticModel.GetTypeInfo(expression, token);
		builder.Header("type", TypeText(typeInfo.Type, typeInfo.Nullability.Annotation));
		if (typeInfo.ConvertedType is not null && !SymbolEqualityComparer.IncludeNullability.Equals(typeInfo.Type, typeInfo.ConvertedType))
			builder.Header("convertedType", TypeText(typeInfo.ConvertedType, typeInfo.ConvertedNullability.Annotation));
		builder.Header("nullability", $"{typeInfo.Nullability.Annotation}/{typeInfo.Nullability.FlowState}");

		if (ConversionText(semanticModel, expression, typeInfo, token) is string conversion)
			builder.Header("conversion", conversion);

		Optional<object?> constant = semanticModel.GetConstantValue(expression, token);
		builder.Header("constant", constant.HasValue ? ConstantText(constant.Value) : Unavailable);

		SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(expression, token);
		ISymbol? symbol = symbolInfo.Symbol;
		if (symbol is null)
		{
			builder.Header("symbol", Unavailable);
			if (symbolInfo.CandidateReason != CandidateReason.None)
				builder.Header("candidateReason", symbolInfo.CandidateReason.ToString());
			if (!symbolInfo.CandidateSymbols.IsEmpty)
			{
				builder.Header(
					"candidates",
					string.Join(OutlineBuilder.LocationSeparator, symbolInfo.CandidateSymbols.Select(candidate => SymbolName(Requeryable(candidate))).Distinct(StringComparer.Ordinal)));
			}

			return builder.ToString();
		}

		AppendSymbol(builder, solutionDirectory, symbol, token);
		return builder.ToString();
	}

	private static void AppendSymbol(OutlineBuilder builder, string? solutionDirectory, ISymbol symbol, CancellationToken token)
	{
		ISymbol requeryable = Requeryable(symbol);
		builder.Header("symbol", SymbolName(requeryable));
		builder.Header("symbolKind", SymbolKindText.Of(requeryable));
		if (!SymbolEqualityComparer.Default.Equals(symbol, requeryable))
			builder.Header("instantiation", symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));

		Location? location = requeryable.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is not null)
		{
			FileLinePositionSpan symbolSpan = location.GetDisplaySpan();
			builder.Header("origin", "source");
			builder.Header("symbolPath", SolutionRelativePath.Of(solutionDirectory, symbolSpan.Path));
			builder.Header("symbolLoc", $"{symbolSpan.StartLinePosition.Line + 1}:{symbolSpan.StartLinePosition.Character + 1}");
		}
		else if (requeryable.ContainingAssembly is { } assembly)
		{
			builder.Header("origin", "metadata");
			builder.Header("assembly", assembly.Name);
		}

		if (DocumentationSummary(requeryable, token) is string doc)
			builder.Header("doc", doc);
	}

	/// <summary>The declaration a name-based tool can find: generic definitions, and extension methods in their static form.</summary>
	private static ISymbol Requeryable(ISymbol symbol) =>
		symbol switch
		{
			IMethodSymbol { ReducedFrom: { } reducedFrom } => reducedFrom.OriginalDefinition,
			_ => symbol.OriginalDefinition
		};

	private static string SymbolName(ISymbol symbol) =>
		symbol.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.RangeVariable or SymbolKind.Label or SymbolKind.Discard
			? symbol.Name
			: SymbolResolver.SignatureName(symbol);

	private static string TypeText(ITypeSymbol? type, NullableAnnotation annotation)
	{
		if (type is null)
			return Unavailable;

		string text = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
			.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
		return annotation == NullableAnnotation.Annotated && type.IsReferenceType && !text.EndsWith('?')
			? text + "?"
			: text;
	}

	private static string? ConversionText(SemanticModel semanticModel, ExpressionSyntax expression, TypeInfo typeInfo, CancellationToken token)
	{
		if (typeInfo.ConvertedType is null)
			return null;

		Microsoft.CodeAnalysis.CSharp.Conversion conversion = semanticModel.GetConversion(expression, token);
		if (!conversion.Exists)
			return "none";
		if (conversion.IsIdentity)
			return SymbolEqualityComparer.Default.Equals(typeInfo.Type, typeInfo.ConvertedType) ? null : "identity";

		var parts = new List<string> { conversion.IsImplicit ? "implicit" : "explicit" };
		if (conversion.IsNullable) parts.Add("nullable");
		if (conversion.IsNumeric) parts.Add("numeric");
		if (conversion.IsEnumeration) parts.Add("enumeration");
		if (conversion.IsReference) parts.Add("reference");
		if (conversion.IsBoxing) parts.Add("boxing");
		if (conversion.IsUnboxing) parts.Add("unboxing");
		if (conversion.IsNullLiteral) parts.Add("null-literal");
		if (conversion.IsDefaultLiteral) parts.Add("default-literal");
		if (conversion.IsConstantExpression) parts.Add("constant");
		if (conversion.IsAnonymousFunction) parts.Add("anonymous-function");
		if (conversion.IsMethodGroup) parts.Add("method-group");
		if (conversion.IsInterpolatedString) parts.Add("interpolated-string");
		if (conversion.IsInterpolatedStringHandler) parts.Add("interpolated-string-handler");
		if (conversion.IsTupleLiteralConversion || conversion.IsTupleConversion) parts.Add("tuple");
		if (conversion.IsStackAlloc) parts.Add("stackalloc");
		if (conversion.IsSwitchExpression) parts.Add("switch-expression");
		if (conversion.IsConditionalExpression) parts.Add("conditional-expression");
		if (conversion.IsObjectCreation) parts.Add("target-typed-new");
		if (conversion.IsCollectionExpression) parts.Add("collection-expression");
		if (conversion.IsPointer) parts.Add("pointer");
		if (conversion.IsUserDefined)
		{
			parts.Add("user-defined");
			if (conversion.MethodSymbol is { } method)
				parts.Add(method.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
		}

		return string.Join(' ', parts);
	}

	private static string ConstantText(object? value) =>
		value is null
			? "null"
			: SymbolDisplay.FormatPrimitive(value, quoteStrings: true, useHexadecimalNumbers: false) ?? value.ToString() ?? Unavailable;

	private static string? DocumentationSummary(ISymbol symbol, CancellationToken token)
	{
		string? xml = symbol.GetDocumentationCommentXml(expandIncludes: true, cancellationToken: token);
		if (string.IsNullOrWhiteSpace(xml))
			return null;

		try
		{
			XElement? summary = XElement.Parse(xml).Element("summary") ?? XDocument.Parse($"<r>{xml}</r>").Root?.Descendants("summary").FirstOrDefault();
			if (summary is null)
				return null;

			string text = string.Concat(summary.Nodes().Select(NodeText));
			text = CollapseWhitespace(text);
			return text.Length == 0 ? null : Truncate(text, MaxDocumentationLength);
		}
		catch (System.Xml.XmlException)
		{
			return null;
		}
	}

	/// <summary>Plain text of a doc node: a cref/langword/paramref reference renders as the name it points at.</summary>
	private static string NodeText(XNode node) =>
		node switch
		{
			XText text => text.Value,
			XElement element when element.Attribute("cref") is { } cref => CrefText(cref.Value),
			XElement element when element.Attribute("langword") is { } langword => langword.Value,
			XElement element when element.Attribute("name") is { } name && !element.Nodes().Any() => name.Value,
			XElement element => string.Concat(element.Nodes().Select(NodeText)),
			_ => ""
		};

	/// <summary>A cref's short name: 'T:System.Int32' reads as 'Int32', 'M:N.Type.Method(System.Int32)' as 'Type.Method'.</summary>
	private static string CrefText(string cref)
	{
		bool isType = cref.StartsWith("T:", StringComparison.Ordinal);
		string name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
		int parameters = name.IndexOf('(');
		if (parameters >= 0)
			name = name[..parameters];

		string[] segments = name.Split('.');
		return string.Join('.', segments.Skip(Math.Max(0, segments.Length - (isType ? 1 : 2))));
	}

	private static string CollapseWhitespace(string text) =>
		Whitespace().Replace(text, " ").Trim();

	private static string Truncate(string text, int maxLength) =>
		text.Length <= maxLength ? text : text[..maxLength] + "...";

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
