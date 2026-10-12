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

	[McpServerTool(
		Name = FindDeadConditionalsName,
		Title = "Find never-compiled #if branches",
		ReadOnly = true,
		Idempotent = true,
		Destructive = false,
		OpenWorld = false)]
	[Description(
		"""
		Flags #if / #elif / #else branches whose region is never compiled in any known configuration - a
		likely typo'd or stale symbol, or intentionally-disabled code. Each condition is evaluated against
		the configurations the solution actually builds (each project's symbols, and the same set minus
		DEBUG), across all target frameworks; a branch taken by none is reported, grouped by file. Also
		use find_dead_code to find unreferenced symbols.
		  #deadConditionals=<n>

		  <relative/forward-slash/path.cs>
		  \t<line:col>,<directive>,<condition>
		where directive is if|elif|else and condition is the raw #if expression ('(else)' for #else).
		A branch defined only in a configuration not loaded here (for example a target framework whose
		workload is missing, or a CI-injected define) may be a false positive, so treat the result as
		'possible' dead code.
		""")]
	public async Task<string> FindDeadConditionals(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description(ParameterDescriptions.StartItem)] int startItem = 1,
		[Description(ParameterDescriptions.MaxSize)] int maxSize = 0)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = await instance.ReadModelAsync();
		return await FindDeadConditionalsCoreAsync(model, instance, startItem, maxSize, Budget);
	}

	internal async Task<string> FindDeadConditionalsCoreAsync(
		SolutionModel model,
		RoslynInstance instance,
		int startItem = 1,
		int maxSize = 0,
		ResponseBudget? budget = null,
		CancellationToken token = default)
	{
		if (model.Solution is null)
			return OutlineError.Format(Error.Indexing(), model.Status);

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
			budget);
		return render switch
		{
			PagedRender.Complete complete => complete.Output,
			PagedRender.Page paged => paged.Output,
			PagedRender.Invalid invalid => OutlineError.Format(Error.Invalid(invalid.Message), model.Status),
			_ => throw new InvalidOperationException($"Unexpected paged render {render.GetType().Name}."),
		};
	}
}
