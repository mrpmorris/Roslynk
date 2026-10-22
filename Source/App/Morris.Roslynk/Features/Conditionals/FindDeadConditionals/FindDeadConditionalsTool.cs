using System.ComponentModel;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;

namespace Morris.Roslynk.Features.Conditionals.FindDeadConditionals;

[McpServerToolType]
public sealed class FindDeadConditionalsTool
{
	public const string FindDeadConditionalsName = "find_dead_conditionals";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly ConditionalCoverage ConditionalCoverage;
	private readonly ResponseBudget Budget;

	public FindDeadConditionalsTool(InstanceRegistry instanceRegistry, ConditionalCoverage conditionalCoverage, ResponseBudget? responseBudget = null)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		ConditionalCoverage = conditionalCoverage ?? throw new ArgumentNullException(nameof(conditionalCoverage));
		Budget = responseBudget ?? ResponseBudget.Default;
	}

	[Description("""
		SupportsPaging=Y

		Finds #if/#elif/#else branches absent from every checked configuration: each project's symbols
		and those symbols without DEBUG, across loaded frameworks. Results are possible dead code;
		missing workloads or CI-only defines can cause false positives.

		Header: deadConditionals=<n>. Body grouped by file; leaves: <line:col>,<directive>,<condition>.
		directive=if|elif|else; condition is the raw expression or '(else)'.
		Use find_dead_code for unreferenced symbols.
		""" + "\n\n" + OutlineDescriptions.CountOnlyOutput)]
	[McpServerTool(
		Name = FindDeadConditionalsName,
		Title = "Find never-compiled #if branches",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	public async Task<string> FindDeadConditionals(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0,
		[Description(ParameterDescriptions.CountOnly)] bool countOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = await instance.ReadModelAsync();
		return await FindDeadConditionalsCoreAsync(model, instance, startItem, maxSize, countOnly, Budget);
	}

	internal async Task<string> FindDeadConditionalsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		int startItem = 1,
		int maxSize = 0,
		bool countOnly = false,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(instance.LoadedProjects), model.Status);

		string? solutionDirectory = SolutionRelativePath.DirectoryOf(model.Solution);
		IReadOnlyList<DeadConditional> dead = await ConditionalCoverage.FindNeverBuiltAsync(model.Solution);

		IEnumerable<IGrouping<string, DeadConditional>> byFile = dead
			.GroupBy(item => SolutionRelativePath.Of(solutionDirectory, item.FilePath) ?? item.FilePath)
			.OrderBy(group => group.Key, StringComparer.Ordinal);

		void RenderBody(OutlineBuilder target)
		{
			foreach (IGrouping<string, DeadConditional> file in byFile)
			{
				target.Line(0, file.Key);
				foreach (DeadConditional item in file.OrderBy(entry => entry.Line).ThenBy(entry => entry.Column))
					target.Line(1, $"{item.Line}:{item.Column},{item.Directive},{OutlineBuilder.Field(item.Condition)}");
			}
		}

		// Items for the shared pager: one entry each, its file line as the open container.
		var items = new List<OutlineItem>();
		foreach (IGrouping<string, DeadConditional> file in byFile)
		{
			foreach (DeadConditional item in file.OrderBy(entry => entry.Line).ThenBy(entry => entry.Column))
				items.Add(OutlineItem.Single($"{item.Line}:{item.Column},{item.Directive},{OutlineBuilder.Field(item.Condition)}", [file.Key]));
		}

		PagedRender render = ItemPaging.Render(
			model.Status,
			addHeaders: builder => builder.Header("deadConditionals", dead.Count),
			renderBody: RenderBody,
			items,
			startItem,
			maxSize,
			budget,
			countOnly: countOnly);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => OutlineError.Format(Error.Invalid(invalid.Message), model.Status),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
