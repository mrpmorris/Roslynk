using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Callers;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Callers.GetCallees;

[McpServerToolType]
public sealed class GetCalleesTool
{
	public const string GetCalleesName = "get_callees";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;
	private readonly ProjectionService ProjectionService;

	public GetCalleesTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[McpServerTool(
		Name = GetCalleesName,
		Title = "Get the members a member calls",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Finds the methods, constructors, property or event accessors and operators that the resolved member's
		code calls (by fully-qualified name) - the inverse of get_callers, answering "what does this call?".
		{OutlineDescriptions.ProjectionCoverage}
		{OutlineDescriptions.CommonMethodInstructions}
		Callees in the solution are grouped like get_callers; callees declared in a referenced assembly (BCL,
		NuGet) are grouped under one top-level '<external:AssemblyName>' line per assembly instead of a project:
		  resolvedSymbol=<name, parameter types included for a method or indexer>

		  <external:AssemblyName>
		  \t<namespace>
		  \t\t<typeKind>,<typeName>
		  \t\t\t<memberKind>,<memberName>,<empty loc>[,<parameterTypes>]
		  <project>
		  \t<relative/forward-slash/folder>
		  \t\t<file.cs|file.razor>
		  \t\t\t<namespace>
		  \t\t\t\t<typeKind>,<typeName>
		  \t\t\t\t\t<memberKind>,<memberName>,<loc>[,<parameterTypes>]
		where kind is one of {OutlineDescriptions.KindList} and {OutlineDescriptions.Loc}; {OutlineDescriptions.ListFieldQuoting}.
		An external callee's loc field is present but empty. parameterTypes appears only when the callee's type
		declares more than one method of that name (inherited overloads do not count): the declared parameter
		types, pipe-delimited, without ref/out/in, or '()' for the parameterless overload.
		Each callee is reported as declared: generic instantiations (List<int>.Add, To<string>) collapse to the
		generic definition, and an extension method lists its 'this' parameter.
		The member may be a method, constructor, operator, property, indexer, event or field. A property read or
		write names the accessor that runs (get or set; a compound assignment or increment reads and writes, so
		both appear), and 'e += h' names the event's add accessor. User-defined operators and conversions, the
		Dispose a using runs, and a method group passed as a delegate are included. The plumbing the compiler
		inserts for foreach (GetEnumerator, MoveNext, Current) and await (GetAwaiter, IsCompleted, GetResult)
		is not; an explicit .GetAwaiter() call is.
		A constructor also reports the calls in the field and property initializers it runs. A property is its
		accessors, a field its initializer. Lambdas and local functions declared inside the member belong to
		it. A member with no code in the solution (an abstract or interface declaration, an auto-property)
		calls nothing.
		{OutlineDescriptions.Project} {OutlineDescriptions.FilePathSplit} {OutlineDescriptions.ErrorBlock} Prefer this over reading the body and tracing each
		call by eye: the compiler's operation model binds every call to the overload that actually runs.
		"""
	)]
	public async Task<string> GetCallees(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description($"Fully-qualified name of the member whose calls are listed, e.g. 'MyNamespace.MyType.MyMethod'. {OutlineDescriptions.SymbolNameGrammar}")] string memberName,
		[Description("Leave out callees declared in referenced assemblies (BCL, NuGet), listing only the solution's own members.")] bool excludeExternal = false)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await GetCalleesCoreAsync(model, instance, memberName, excludeExternal, CancellationToken.None);
	}

	internal async Task<string> GetCalleesCoreAsync(SolutionModel model, RoslynInstance instance, string memberName, bool excludeExternal = false, CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		IReadOnlyList<IReadOnlyList<ProjectionSymbol>> groups = await ProjectionService.ResolveAsync(SymbolResolver, projections, memberName);

		if (groups.Count == 0)
		{
			IReadOnlyList<string> candidates = await SymbolResolver.SuggestAsync(model.Solution, memberName);
			return Failure(Error.NotFound($"No symbol matched '{memberName}'.", candidates.Count > 0 ? candidates : null));
		}

		if (groups.Count > 1)
			return Failure(SymbolAmbiguity.Ambiguous(memberName, groups.Select(group => group[0].Symbol)));

		IReadOnlyList<ProjectionSymbol> resolved = groups[0];

		// Union callees across every projection, deduped by stable symbol identity, so a callee that only
		// compiles in a branch inactive in the loaded configuration is still reported.
		var seen = new SymbolIdentityIndex();
		var root = new SymbolNode();
		foreach (ProjectionSymbol projectionSymbol in resolved)
		{
			foreach (ISymbol callee in await CalleeWalker.CalleesOfAsync(projectionSymbol.Symbol, projectionSymbol.Projection.Solution, token))
			{
				if (excludeExternal && CalleePlacement.IsExternal(callee))
					continue;

				if (seen.Add(callee))
					CalleePlacement.Place(root, callee, projectionSymbol.Projection.Solution, solutionDirectory);
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("resolvedSymbol", SymbolResolver.SignatureName(resolved[0].Symbol));
		builder.Status(model.Status);
		builder.BeginBody();
		root.Render(builder);
		return builder.ToString();
	}
}
