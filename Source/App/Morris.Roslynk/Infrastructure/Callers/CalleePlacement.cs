using Microsoft.CodeAnalysis;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Infrastructure.Callers;

/// <summary>
/// Places one callee into get_callees' outline. A source callee goes under project -> file -> namespace ->
/// containing type(s) like <see cref="SymbolPlacement"/>; an external one (declared in a referenced assembly)
/// goes under a top-level <c>&lt;external:AssemblySimpleName&gt;</c> bucket instead, so each dependency
/// assembly reads as a peer of the solution's projects. Every member leaf carries the loc field, empty for an
/// external callee, and a fourth field with the parameter types when the callee's type declares more than
/// one method of that name (<c>()</c> for the parameterless overload) — the only way to tell overloads apart
/// when there is no location.
/// </summary>
public static class CalleePlacement
{
	public const string ExternalBucket = "<external>";

	public static bool IsExternal(ISymbol symbol) => !symbol.Locations.Any(location => location.IsInSource);

	public static void Place(SymbolNode root, ISymbol callee, Solution solution, string? solutionDirectory)
	{
		Location? location = callee.Locations.FirstOrDefault(candidate => candidate.IsInSource);

		SymbolNode node;
		if (location?.SourceTree is SyntaxTree tree && location.SourceTree.FilePath is string path)
		{
			SymbolNode start = ProjectName.Of(solution, tree) is string project ? root.Child(project) : root;
			node = start.ChildPath(SolutionRelativePath.Of(solutionDirectory, path)!);
		}
		else
		{
			// Assembly names are taken verbatim: they come from the solution's own references.
			node = root.Child(callee.ContainingAssembly is IAssemblySymbol assembly
				? $"<external:{assembly.Name}>"
				: ExternalBucket);
		}

		node = node.Child(SymbolPlacement.NamespaceOf(callee));

		var parents = new List<INamedTypeSymbol>();
		for (INamedTypeSymbol? containing = callee.ContainingType; containing is not null; containing = containing.ContainingType)
			parents.Insert(0, containing);

		foreach (INamedTypeSymbol parent in parents)
			node = node.Child($"{SymbolKindText.Of(parent)},{OutlineBuilder.Field(parent.Name)}");

		// A local function nests under the member (and any outer local functions) declaring it.
		foreach (ISymbol container in LocalFunctions.ContainerChain(callee))
			node = node.Child($"{SymbolKindText.Of(container)},{OutlineBuilder.Field(container.Name)}");

		string key = $"{SymbolKindText.Of(callee)},{OutlineBuilder.Field(callee.Name)}";
		string? parameters = callee is IMethodSymbol method && IsOverloaded(method)
			? method.Parameters.Length == 0 ? "()" : ParameterTypes.Of(method)
			: null;

		if (location is null)
		{
			node.Child(parameters is null ? $"{key}," : $"{key},,{parameters}");
			return;
		}

		FileLinePositionSpan span = location.GetDisplaySpan();
		int line = span.StartLinePosition.Line + 1;
		int column = span.StartLinePosition.Character + 1;
		int endLine = span.EndLinePosition.Line + 1;
		int endColumn = span.EndLinePosition.Character + 1;

		if (parameters is null)
			node.Child(key).AddLocation(line, column, endLine, endColumn);
		else
			node.AddLeaf(key, line, column, endLine, endColumn, parameters);
	}

	/// <summary>The callee's own type declares more than one method of its name; inherited ones do not count.</summary>
	private static bool IsOverloaded(IMethodSymbol method) =>
		method.ContainingType is INamedTypeSymbol type
		&& type.GetMembers(method.Name).OfType<IMethodSymbol>().Skip(1).Any();
}
