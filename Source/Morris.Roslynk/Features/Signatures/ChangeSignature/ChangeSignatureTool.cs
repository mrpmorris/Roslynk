using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.Signatures.ChangeSignature;

[McpServerToolType]
public sealed class ChangeSignatureTool
{
	public const string ChangeSignatureName = "change_signature";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ApplyPipeline ApplyPipeline;

	public ChangeSignatureTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[McpServerTool(
		Name = ChangeSignatureName,
		Title = "Add a parameter to a method",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		"""
		Adds a new optional parameter to a method and, if a call-site value is given, threads it through every
		invocation as a named argument — collapsing the repeated add-a-parameter cascades that are otherwise
		redone by hand across files. The parameter must have a default so the change stays backward-compatible.
		v1 targets a single ordinary method only: it refuses virtual/override/abstract methods, interface
		members and their implementations, partial methods, params methods, and constructors (returns Not
		Supported). Pass checkOnly to preview the changed files without writing.
		""")]
	public async Task<ChangeSignatureResult> ChangeSignature(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Fully-qualified name of the method to change, e.g. Namespace.Type.Method.")] string methodId,
		[Description("The new parameter's type, e.g. System.Threading.CancellationToken.")] string parameterType,
		[Description("The new parameter's name (a valid C# identifier).")] string parameterName,
		[Description("The parameter's default value expression, e.g. default, null, 0. Required (the parameter is optional).")] string defaultValue,
		[Description("Optional expression to pass at every call site as a named argument. If omitted, calls use the default.")] string? callSiteArgument = null,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		ChangeSignatureResult Success(bool applied, string resolvedMethod, IReadOnlyList<string> changed, int callSiteCount) =>
			new(model, error: null, applied, resolvedMethod, changed, callSiteCount);

		ChangeSignatureResult Failure(Error error) =>
			new(model, error, applied: false, resolvedMethod: null, changedFiles: null, updatedCallSites: 0);

		ChangeSignatureResult NotSupported(string resolvedMethod, string message) =>
			new(model, Error.NotSupported(message), applied: false, resolvedMethod, changedFiles: null, updatedCallSites: 0);

		if (!SyntaxFacts.IsValidIdentifier(parameterName))
			return Failure(Error.Invalid($"'{parameterName}' is not a valid C# identifier."));
		if (string.IsNullOrWhiteSpace(parameterType))
			return Failure(Error.Invalid("A parameter type is required."));
		if (string.IsNullOrWhiteSpace(defaultValue))
			return Failure(Error.Invalid("A default value is required so the added parameter is optional and the change stays backward-compatible."));

		if (model.Solution is null)
			return Failure(Error.Indexing());

		Solution solution = model.Solution;

		IReadOnlyList<ISymbol> matches = await SymbolResolver.FindByFullyQualifiedNameAsync(solution, methodId);
		if (matches.Count == 0)
		{
			IReadOnlyList<string> suggestions = await SymbolResolver.SuggestAsync(solution, methodId);
			return Failure(Error.NotFound($"No symbol matched '{methodId}'.", suggestions.Count > 0 ? suggestions : null));
		}
		if (matches.Count > 1)
		{
			string[] candidates = matches.Select(SymbolResolver.FullyQualifiedName).Distinct(StringComparer.Ordinal).ToArray();
			return Failure(Error.Ambiguous($"'{methodId}' is ambiguous ({matches.Count} matches, likely overloads); v1 cannot target a specific overload.", candidates));
		}
		if (matches[0] is not IMethodSymbol method)
			return Failure(Error.NotSupported($"'{methodId}' is not a method."));

		string resolved = SymbolResolver.FullyQualifiedName(method);

		string? rejection = Reject(method, parameterName);
		if (rejection is not null)
			return NotSupported(resolved, rejection);

		SyntaxNode declarationNode = await method.DeclaringSyntaxReferences[0].GetSyntaxAsync();
		if (declarationNode is not MethodDeclarationSyntax methodDeclaration)
			return NotSupported(resolved, "The method is not an ordinary method declaration.");

		Document declarationDocument = solution.GetDocument(methodDeclaration.SyntaxTree)!;

		IReadOnlyList<CallSite> callSites = callSiteArgument is null
			? []
			: await FindCallSitesAsync(solution, method);

		ParameterSyntax parameter = ((ParameterListSyntax)SyntaxFactory.ParseParameterList(
			$"({parameterType} {parameterName} = {defaultValue})")).Parameters[0].WithLeadingTrivia(SyntaxFactory.Space);
		MethodDeclarationSyntax newDeclaration = methodDeclaration.WithParameterList(methodDeclaration.ParameterList.AddParameters(parameter));

		var editor = new SolutionEditor(solution);
		DocumentEditor declarationEditor = await editor.GetDocumentEditorAsync(declarationDocument.Id);
		declarationEditor.ReplaceNode(methodDeclaration, newDeclaration);

		foreach (IGrouping<DocumentId, CallSite> group in callSites.GroupBy(callSite => callSite.DocumentId))
		{
			DocumentEditor documentEditor = await editor.GetDocumentEditorAsync(group.Key);
			foreach (CallSite callSite in group)
			{
				ArgumentSyntax argument = ((ArgumentListSyntax)SyntaxFactory.ParseArgumentList(
					$"({parameterName}: {callSiteArgument})")).Arguments[0].WithLeadingTrivia(SyntaxFactory.Space);
				documentEditor.ReplaceNode(callSite.Invocation, callSite.Invocation.WithArgumentList(callSite.Invocation.ArgumentList.AddArguments(argument)));
			}
		}

		Solution updated = editor.GetChangedSolution();

		if (checkOnly)
		{
			IReadOnlyList<string> preview = ApplyPipeline.GetChangedFilePaths(solution, updated);
			return Success(applied: false, resolved, preview, callSites.Count);
		}

		IReadOnlyList<string> changed = await ApplyPipeline.ApplyAsync(instance, updated);
		return Success(applied: true, resolved, changed, callSites.Count);
	}

	private static string? Reject(IMethodSymbol method, string parameterName)
	{
		if (method.MethodKind != MethodKind.Ordinary)
			return "Only ordinary methods are supported (not constructors, operators, accessors, or local functions).";
		if (method.IsVirtual || method.IsOverride || method.IsAbstract)
			return "Virtual, override, and abstract methods are not supported; their signatures must change together across the hierarchy.";
		if (method.ContainingType.TypeKind == TypeKind.Interface)
			return "Interface members are not supported; the interface and every implementation must change together.";
		if (method.ExplicitInterfaceImplementations.Length > 0 || ImplementsInterfaceMember(method))
			return "Methods that implement an interface member are not supported; the interface and implementation must change together.";
		if (method.DeclaringSyntaxReferences.Length != 1)
			return "Partial methods (more than one declaration) are not supported.";
		if (method.Parameters.Any(parameter => parameter.IsParams))
			return "Methods with a params parameter are not supported; an optional parameter cannot be appended after it.";
		if (method.Parameters.Any(parameter => string.Equals(parameter.Name, parameterName, StringComparison.Ordinal)))
			return $"The method already has a parameter named '{parameterName}'.";

		return null;
	}

	private static bool ImplementsInterfaceMember(IMethodSymbol method)
	{
		foreach (INamedTypeSymbol @interface in method.ContainingType.AllInterfaces)
		{
			foreach (ISymbol member in @interface.GetMembers())
			{
				if (member is IMethodSymbol && SymbolEqualityComparer.Default.Equals(
					method.ContainingType.FindImplementationForInterfaceMember(member), method))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static async Task<IReadOnlyList<CallSite>> FindCallSitesAsync(Solution solution, IMethodSymbol method)
	{
		var callSites = new List<CallSite>();
		foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(method, solution))
		{
			foreach (ReferenceLocation location in referenced.Locations)
			{
				if (location.Location.SourceTree is null)
					continue;

				Document? document = solution.GetDocument(location.Location.SourceTree);
				if (document is null)
					continue;

				SyntaxNode root = await location.Location.SourceTree.GetRootAsync();
				SyntaxNode node = root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);

				// A real call site is one where the reference sits in the callee position of an invocation;
				// method-group, nameof, and cref uses stay valid because the added parameter is optional.
				InvocationExpressionSyntax? invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
				if (invocation is not null && invocation.Expression.Span.Contains(location.Location.SourceSpan))
					callSites.Add(new CallSite(document.Id, invocation));
			}
		}

		return callSites;
	}

	private readonly record struct CallSite(DocumentId DocumentId, InvocationExpressionSyntax Invocation);
}
