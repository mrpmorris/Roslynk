using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Razor;
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
	private readonly ProjectionService ProjectionService;

	public FindDefinitionTool(InstanceRegistry instanceRegistry, SymbolResolver symbolResolver, ProjectionService projectionService)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		SymbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
		ProjectionService = projectionService ?? throw new ArgumentNullException(nameof(projectionService));
	}

	[Description("""
		Resolves the symbol at a source position. Returns fullName (including method/indexer parameter
		types) and kind; source symbols add project, path and loc, metadata symbols add assembly.
		No binding in any projection returns NotFound.
		""")]
	[McpServerTool(
		Name = FindDefinitionName,
		Title = "Find a symbol's definition",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindDefinition(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("Usage's .cs file path.")] string filePath,
		[Description("Usage line.")] int line,
		[Description("Usage column.")] int column)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await FindDefinitionCoreAsync(model, instance, filePath, line, column, CancellationToken.None);
	}

	internal async Task<string> FindDefinitionCoreAsync(SolutionModel model, RoslynInstance instance, string filePath, int line, int column, CancellationToken token = default)
	{
		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);

		// Resolve in every projection: a position inside a branch inactive in the loaded configuration resolves
		// to no symbol in the base projection but binds in the projection where that branch is active, and a
		// copy whose parameter types failed to bind must not win over one where they bind. A bound answer
		// stops the walk, so a position that binds in the first projection costs exactly one resolution.
		IReadOnlyList<Projection> projections = await ProjectionService.BuildAsync(model.Solution);
		ISymbol? symbol = null;
		Solution resolvedSolution = model.Solution;
		int bestErrors = int.MaxValue;
		foreach (Projection projection in projections)
		{
			ISymbol? resolved = await SymbolResolver.ResolveAtPositionAsync(projection.Solution, filePath, line, column);
			if (resolved is null)
				continue;

			int errors = ErrorTypeCount.Of(resolved);
			if (errors < bestErrors)
			{
				symbol = resolved;
				resolvedSolution = projection.Solution;
				bestErrors = errors;
			}

			if (bestErrors == 0)
				break;
		}

		if (symbol is null)
			return Failure(Error.NotFound($"No symbol resolved at {filePath} ({line}, {column})."));

		var builder = new OutlineBuilder();
		builder.Header("fullName", SymbolResolver.SignatureName(symbol));
		builder.Header("kind", SymbolKindText.Of(symbol));

		Location? location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
		if (location is null)
		{
			if (symbol.ContainingAssembly is { } assembly)
				builder.Header("assembly", assembly.Name);

			return builder.ToString();
		}

		FileLinePositionSpan span = location.GetDisplaySpan();
		if (ProjectName.Of(resolvedSolution, location.SourceTree!) is string project)
			builder.Header("project", project);
		builder.Header("path", SolutionRelativePath.Of(solutionDirectory, span.Path)!);
		builder.Header("loc", $"{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}-{span.EndLinePosition.Line + 1}:{span.EndLinePosition.Character + 1}");
		GeneratedSource.AppendHeaders(builder, resolvedSolution, location.SourceTree);
		return builder.ToString();
	}
}
