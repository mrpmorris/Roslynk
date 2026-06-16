using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Symbols.FindDefinition;

[McpServerToolType]
public sealed class FindDefinitionTool
{
	public const string FindDefinitionName = "find_definition";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly SymbolResolver SymbolResolver;

	public FindDefinitionTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
	}

	[McpServerTool(
		Name = FindDefinitionName,
		Title = "Find a symbol's definition",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		$"""
		Resolves the symbol used at a source position (file, 1-based line and column) and returns where it is
		declared; the 'go to definition' jump, by position. {OutlineDescriptions.TextNotJson} The result is a
		'#fullName', '#kind' header plus '#project=<project>', '#path=<relative/path.cs>' and '#loc=<line:col>' for a source symbol,
		or '#assembly=<name>' for a metadata symbol. {OutlineDescriptions.Project}. {OutlineDescriptions.ErrorBlock} Prefer this over grepping
		to chase a definition; it follows the compiler's binding, so it lands on the right symbol even when
		names are overloaded or shadowed.
		""")]
	public async Task<string> FindDefinition(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path to the .cs file containing the usage; absolute, or relative to the solution folder.")] string filePath,
		[Description("1-based line of the usage.")] int line,
		[Description("1-based column of the usage.")] int column)
	{
		RoslynInstance instance = InstanceRegistry.GetOrBegin(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		ISymbol? symbol = await SymbolResolver.ResolveAtPositionAsync(model.Solution, filePath, line, column);
		if (symbol is null)
			return Failure(Error.NotFound($"No symbol resolved at {filePath} ({line}, {column})."));

		var builder = new OutlineBuilder();
		builder.Header("fullName", SymbolResolver.FullyQualifiedName(symbol));
		builder.Header("kind", SymbolKindText.Of(symbol));

		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
		{
			if (symbol.ContainingAssembly is { } assembly)
				builder.Header("assembly", assembly.Name);

			return builder.ToString();
		}

		FileLinePositionSpan span = location.GetLineSpan();
		if (ProjectName.Of(model.Solution, location.SourceTree!) is string project)
			builder.Header("project", project);
		builder.Header("path", SolutionRelativePath.Of(solutionDirectory, span.Path)!);
		builder.Header("loc", $"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}");
		return builder.ToString();
	}
}
