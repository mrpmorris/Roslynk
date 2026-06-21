using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Results;
using Morris.Roslynk.Infrastructure.Workspaces;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.CodeActions.ApplyCodeAction;

[McpServerToolType]
public sealed class ApplyCodeActionTool
{
	public const string ApplyCodeActionName = "apply_code_action";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly CodeActionService CodeActionService;
	private readonly ApplyPipeline ApplyPipeline;

	public ApplyCodeActionTool(InstanceRegistry instanceRegistry, CodeActionService codeActionService, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		CodeActionService = codeActionService ?? throw new ArgumentNullException(nameof(codeActionService));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[McpServerTool(
		Name = ApplyCodeActionName,
		Title = "Apply a code action",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		$"""
		Applies a code action discovered by get_code_actions, identified by its actionId. Returns a text
		result, not JSON: '#applied', '#action', '#status' header, a blank line, then one
		solution-relative changed-file path per line. {OutlineDescriptions.Project} The action is re-resolved at the same position (nothing is
		held between calls), then written atomically through the same safe write path as the other tools. Pass
		checkOnly to preview the changed files without writing. Prefer applying Roslyn's action over
		re-implementing the change by hand so the in-memory model stays in sync.
		""")]
	public async Task<string> ApplyCodeAction(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("The actionId from get_code_actions.")] string actionId,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing());

		if (!CodeActionService.TryDecodeId(actionId, out ActionRef actionRef))
			return Failure(Error.Invalid("The actionId could not be decoded; get a fresh one from get_code_actions."));

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		Document? document = CodeActionService.FindDocument(solution, actionRef.DocumentPath);
		if (document is null)
			return Failure(Error.NotFound($"'{actionRef.DocumentPath}' is no longer a solution document."));

		Solution? changed = await CodeActionService.ComputeChangedSolutionAsync(document, actionRef, cancellationToken);
		if (changed is null)
			return Failure(Error.Conflict("The action is no longer available; the code may have changed. Re-run get_code_actions."));

		IReadOnlyList<string> files = checkOnly
			? ApplyPipeline.GetChangedFilePaths(solution, changed)
			: await ApplyPipeline.ApplyAsync(instance, changed, cancellationToken);

		var builder = new OutlineBuilder();
		builder.Header("applied", !checkOnly);
		builder.Header("action", actionRef.Key);
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, files, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}
}
