using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>The kinds of source construct that can hide a compiler-inserted call to a target.</summary>
[Flags]
internal enum ImplicitCallScan
{
	None = 0,

	/// <summary>Enumerator Dispose/DisposeAsync; await foreach GetAsyncEnumerator/MoveNextAsync; element conversion.</summary>
	ForEach = 1 << 0,

	/// <summary>Awaiter IsCompleted/GetResult (also the hidden awaits of await foreach/await using).</summary>
	Await = 1 << 1,

	/// <summary>DisposeAsync run by 'await using'.</summary>
	AwaitUsing = 1 << 2,

	/// <summary>Deconstruct in 'x is (a, b)' / switch patterns.</summary>
	PositionalPattern = 1 << 3,

	/// <summary>operator true/false deciding if/while/for/do/switch/?: conditions.</summary>
	Conditions = 1 << 4,

	/// <summary>C# 14 instance op_*Assignment / op_*Increment (instance, not static).</summary>
	CompoundOperators = 1 << 5,

	/// <summary>User-defined conversions applied without a cast (also the foreach element conversion).</summary>
	Conversions = 1 << 6,

	/// <summary>Query-expression methods.</summary>
	Queries = 1 << 7,

	/// <summary>Handler constructor, AppendLiteral, AppendFormatted.</summary>
	InterpolatedStrings = 1 << 8,

	/// <summary>Length/Count/indexer/Slice used by ^ and .. and list/slice patterns.</summary>
	ImplicitIndex = 1 << 9,

	/// <summary>The implicit base() call a derived constructor without an initializer makes.</summary>
	BaseConstructor = 1 << 10
}

/// <summary>
/// Decides, from the resolved target alone (no binding, no I/O), which scans can find compiler-inserted
/// callers of it, which documents may contain such a call, and which syntax nodes to bind. A false hit costs
/// one walk; a false miss loses a caller — the filters are deliberately sound, never complete.
/// </summary>
internal static class ImplicitCallTargets
{
	private static readonly HashSet<string> QueryMethodNames = new(StringComparer.Ordinal)
	{
		"Select", "SelectMany", "Where", "Join", "GroupJoin", "OrderBy", "OrderByDescending",
		"ThenBy", "ThenByDescending", "GroupBy", "Cast"
	};

	private static readonly string[] CompoundOperatorTokens =
	[
		"+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>=", "??=", "++", "--"
	];

	public static ImplicitCallScan ScansFor(ISymbol target)
	{
		ImplicitCallScan scans = ImplicitCallScan.None;
		INamedTypeSymbol? type = target.ContainingType;

		if (type is not null && IsInterpolatedStringHandler(type))
			scans |= ImplicitCallScan.InterpolatedStrings;

		switch (target)
		{
			// FindCallersAsync finds explicit casts only; a foreach applies the element conversion too.
			case IMethodSymbol { MethodKind: MethodKind.Conversion }:
				return scans | ImplicitCallScan.Conversions | ImplicitCallScan.ForEach;

			case IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, Name: WellKnownMemberNames.TrueOperatorName or WellKnownMemberNames.FalseOperatorName }:
				return scans | ImplicitCallScan.Conditions;

			// C# 14 instance compound/increment operators (op_AdditionAssignment, op_IncrementAssignment, ...).
			case IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, IsStatic: false }:
				return scans | ImplicitCallScan.CompoundOperators;

			case IMethodSymbol { Name: WellKnownMemberNames.DisposeMethodName or WellKnownMemberNames.DisposeAsyncMethodName } method:
				// A plain IDisposable's Dispose is fully covered by FindCallersAsync's using scan — no gate
				// here. Only an enumerator-shaped type's Dispose needs the foreach scan.
				if (type is not null && IsEnumerator(type))
					scans |= ImplicitCallScan.ForEach;
				if (method.Name == WellKnownMemberNames.DisposeAsyncMethodName)
					scans |= ImplicitCallScan.AwaitUsing;
				return scans;

			case IMethodSymbol { Name: WellKnownMemberNames.GetAsyncEnumeratorMethodName or WellKnownMemberNames.MoveNextAsyncMethodName }:
				return scans | ImplicitCallScan.ForEach;

			case IMethodSymbol { Name: WellKnownMemberNames.GetResult } when type is not null && IsAwaiter(type):
				return scans | ImplicitCallScan.Await;

			case IPropertySymbol { Name: "IsCompleted" } when type is not null && IsAwaiter(type):
				return scans | ImplicitCallScan.Await;

			case IMethodSymbol { Name: WellKnownMemberNames.DeconstructMethodName }:
				return scans | ImplicitCallScan.PositionalPattern;

			case IMethodSymbol { Name: var name } when QueryMethodNames.Contains(name):
				return scans | ImplicitCallScan.Queries;

			case IMethodSymbol { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: false }:
				// A derived constructor without an initializer calls base() implicitly; nothing else about a
				// constructor is hidden from the ordinary reference finders.
				return scans | ImplicitCallScan.BaseConstructor;

			case IMethodSymbol { Name: WellKnownMemberNames.SliceMethodName }:
				return scans | ImplicitCallScan.ImplicitIndex;

			case IPropertySymbol { Name: WellKnownMemberNames.LengthPropertyName or WellKnownMemberNames.CountPropertyName }:
				return scans | ImplicitCallScan.ImplicitIndex;

			case IPropertySymbol { IsIndexer: true }:
				return scans | ImplicitCallScan.ImplicitIndex;

			default:
				return scans;
		}
	}

	/// <summary>
	/// A sound text pre-filter: each gated construct needs a token that must appear verbatim, so a document
	/// without it cannot hide a call of that kind. <see cref="ImplicitCallScan.Conversions"/> has no sound
	/// marker (TakeInt(GetMoney()) never names Money) and is never filtered.
	/// </summary>
	public static bool MayContain(SourceText text, ImplicitCallScan scans)
	{
		if (scans == ImplicitCallScan.None)
			return false;

		string source = text.ToString();
		return (!scans.HasFlag(ImplicitCallScan.ForEach) || Contains(source, "foreach"))
			&& (!scans.HasFlag(ImplicitCallScan.Await) || Contains(source, "await"))
			&& (!scans.HasFlag(ImplicitCallScan.AwaitUsing) || Contains(source, "await") && Contains(source, "using"))
			&& (!scans.HasFlag(ImplicitCallScan.PositionalPattern) || ContainsAny(source, "is", "case", "switch"))
			&& (!scans.HasFlag(ImplicitCallScan.Conditions) || ContainsAny(source, "if", "while", "do", "for", "?", "&&", "||"))
			&& (!scans.HasFlag(ImplicitCallScan.CompoundOperators) || ContainsAny(source, CompoundOperatorTokens))
			&& (!scans.HasFlag(ImplicitCallScan.Queries) || Contains(source, "from"))
			&& (!scans.HasFlag(ImplicitCallScan.InterpolatedStrings) || Contains(source, "$"))
			&& (!scans.HasFlag(ImplicitCallScan.ImplicitIndex) || ContainsAny(source, "^", "..", "["));
	}

	/// <summary>
	/// The outermost syntax nodes whose operation tree can carry a call of the scanned kinds — a site nested
	/// inside another site's span is covered by walking that site, so only outermost ones are returned.
	/// </summary>
	public static IReadOnlyList<SyntaxNode> Sites(SyntaxNode root, ImplicitCallScan scans)
	{
		var candidates = new List<SyntaxNode>();

		if (scans.HasFlag(ImplicitCallScan.ForEach))
			candidates.AddRange(root.DescendantNodes().OfType<CommonForEachStatementSyntax>());

		if (scans.HasFlag(ImplicitCallScan.Await))
			candidates.AddRange(root.DescendantNodes().OfType<AwaitExpressionSyntax>());

		if (scans.HasFlag(ImplicitCallScan.AwaitUsing))
		{
			candidates.AddRange(root.DescendantNodes()
				.OfType<UsingStatementSyntax>()
				.Where(usingStatement => usingStatement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)));
			candidates.AddRange(root.DescendantNodes()
				.OfType<LocalDeclarationStatementSyntax>()
				.Where(statement => statement.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)));
		}

		if (scans.HasFlag(ImplicitCallScan.PositionalPattern))
		{
			candidates.AddRange(root.DescendantNodes()
				.OfType<RecursivePatternSyntax>()
				.Where(pattern => pattern.PositionalPatternClause is not null));
		}

		if (scans.HasFlag(ImplicitCallScan.Conditions))
		{
			foreach (SyntaxNode node in root.DescendantNodes())
			{
				switch (node)
				{
					case IfStatementSyntax @if:
						candidates.Add(@if.Condition);
						break;

					case WhileStatementSyntax @while:
						candidates.Add(@while.Condition);
						break;

					case DoStatementSyntax @do:
						candidates.Add(@do.Condition);
						break;

					case ForStatementSyntax @for when @for.Condition is not null:
						candidates.Add(@for.Condition);
						break;

					case SwitchStatementSyntax @switch:
						candidates.Add(@switch.Expression);
						break;

					case ConditionalExpressionSyntax conditional:
						candidates.Add(conditional.Condition);
						break;

					// The op_True/op_False an &&/|| decides with is inserted by lowering; walking the
					// operands costs one bind and reports it only when the operation model carries it.
					case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalAndExpression or (int)SyntaxKind.LogicalOrExpression } binary:
						candidates.Add(binary.Left);
						candidates.Add(binary.Right);
						break;
				}
			}
		}

		if (scans.HasFlag(ImplicitCallScan.CompoundOperators))
		{
			candidates.AddRange(root.DescendantNodes()
				.OfType<AssignmentExpressionSyntax>()
				.Where(assignment => !assignment.OperatorToken.IsKind(SyntaxKind.EqualsToken)));
			candidates.AddRange(root.DescendantNodes()
				.OfType<PrefixUnaryExpressionSyntax>()
				.Where(unary => unary.OperatorToken.IsKind(SyntaxKind.PlusPlusToken) || unary.OperatorToken.IsKind(SyntaxKind.MinusMinusToken)));
			candidates.AddRange(root.DescendantNodes()
				.OfType<PostfixUnaryExpressionSyntax>()
				.Where(unary => unary.OperatorToken.IsKind(SyntaxKind.PlusPlusToken) || unary.OperatorToken.IsKind(SyntaxKind.MinusMinusToken)));
		}

		if (scans.HasFlag(ImplicitCallScan.Queries))
			candidates.AddRange(root.DescendantNodes().OfType<QueryExpressionSyntax>());

		if (scans.HasFlag(ImplicitCallScan.InterpolatedStrings))
			candidates.AddRange(root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>());

		if (scans.HasFlag(ImplicitCallScan.ImplicitIndex))
		{
			candidates.AddRange(root.DescendantNodes()
				.OfType<ElementAccessExpressionSyntax>()
				.Where(IsIndexedFromEndOrRange));
			candidates.AddRange(root.DescendantNodes().OfType<ListPatternSyntax>());
			candidates.AddRange(root.DescendantNodes().OfType<SlicePatternSyntax>());
		}

		// Conversions can appear anywhere executable code is, and no text filter can narrow it: bind every
		// executable root. An interpolated-string handler's calls hang on the same nodes.
		if (scans.HasFlag(ImplicitCallScan.Conversions) || scans.HasFlag(ImplicitCallScan.InterpolatedStrings))
			candidates.AddRange(ExecutableRoots(root));

		return Outermost(candidates);
	}

	private static IReadOnlyList<SyntaxNode> Outermost(List<SyntaxNode> candidates)
	{
		// DescendantNodes yields parents before children, so any node contained in one already accepted is
		// covered by walking that one.
		var accepted = new List<SyntaxNode>();
		foreach (SyntaxNode candidate in candidates)
		{
			if (!accepted.Any(site => site.Span.Contains(candidate.Span)))
				accepted.Add(candidate);
		}

		return accepted;
	}

	private static bool IsIndexedFromEndOrRange(ElementAccessExpressionSyntax element) =>
		element.ArgumentList.Arguments.Any(argument =>
			argument.Expression.IsKind(SyntaxKind.IndexExpression)
			|| argument.Expression.IsKind(SyntaxKind.RangeExpression));

	/// <summary>The declarations whose body this file walks: methods, constructors, operators, accessors with code, and initializers.</summary>
	private static IEnumerable<SyntaxNode> ExecutableRoots(SyntaxNode root)
	{
		foreach (SyntaxNode node in root.DescendantNodes())
		{
			switch (node)
			{
				case MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax:
					yield return node;
					break;

				case AccessorDeclarationSyntax accessor when accessor.Body is not null || accessor.ExpressionBody is not null:
					yield return accessor;
					break;

				case VariableDeclaratorSyntax { Initializer: not null } declarator
					when declarator.Parent?.Parent is FieldDeclarationSyntax or EventFieldDeclarationSyntax:
					yield return declarator;
					break;

				case PropertyDeclarationSyntax { Initializer: not null } property:
					yield return property.Initializer!;
					break;

				case GlobalStatementSyntax global:
					yield return global.Statement;
					break;
			}
		}
	}

	/// <summary>Pattern enumerator: a Current member plus MoveNext/MoveNextAsync, or an IEnumerator implementation.</summary>
	private static bool IsEnumerator(INamedTypeSymbol type) =>
		(type.GetMembers("Current").Any(member => member.Kind == SymbolKind.Property)
			&& (HasMethod(type, "MoveNext") || HasMethod(type, "MoveNextAsync")))
		|| type.AllInterfaces.Any(@interface => @interface.SpecialType == SpecialType.System_Collections_IEnumerator);

	/// <summary>Awaiter pattern: IsCompleted + GetResult, or an INotifyCompletion implementation.</summary>
	private static bool IsAwaiter(INamedTypeSymbol type) =>
		(type.GetMembers("IsCompleted").Any(member => member.Kind == SymbolKind.Property)
			&& HasMethod(type, "GetResult"))
		|| type.AllInterfaces.Any(@interface =>
			string.Equals(@interface.ToDisplayString(), "System.Runtime.CompilerServices.INotifyCompletion", StringComparison.Ordinal));

	private static bool HasMethod(INamedTypeSymbol type, string name) =>
		type.GetMembers(name).Any(member => member is IMethodSymbol);

	private static bool IsInterpolatedStringHandler(INamedTypeSymbol type) =>
		type.GetAttributes().Any(attribute => attribute.AttributeClass is
		{
			Name: "InterpolatedStringHandlerAttribute",
			ContainingNamespace: { Name: "CompilerServices", ContainingNamespace: { Name: "Runtime", ContainingNamespace: { Name: "System", ContainingNamespace: { IsGlobalNamespace: true } } } }
		});

	private static bool Contains(string source, string token) => source.Contains(token, StringComparison.Ordinal);

	private static bool ContainsAny(string source, params string[] tokens) =>
		tokens.Any(token => source.Contains(token, StringComparison.Ordinal));
}
