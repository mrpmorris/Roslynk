using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Documentation;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Outlines;
using Morris.Roslynk.Infrastructure.Razor;
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

	[Description("""
		Applies an actionId from get_code_actions. Re-resolves the action at its original position;
		no action state is retained between calls.

		- Conflict: action no longer resolves after edits; rerun get_code_actions.
		- Invalid: malformed actionId.
		- Stale: a target file changed on disk since loading.

		Razor actions use the shared mapping rules. Adding usings/members to generated scaffolding may
		be unmappable (NotSupported); mismatched Razor text returns Conflict, before saving.

		Returns applied, action and status headers, then a blank line and solution-relative changed paths.
		""")]
	[McpServerTool(
		Name = ApplyCodeActionName,
		Title = "Apply a code action",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	public async Task<string> ApplyCodeAction(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("The actionId from get_code_actions.")] string actionId,
		[Description(ParameterDescriptions.CheckOnly)] bool checkOnly = false,
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

		RazorSourceDocument? source = await RazorSourceDocument.ResolveAsync(solution, actionRef.DocumentPath, cancellationToken);
		if (source is null)
			return Failure(Error.NotFound($"'{actionRef.DocumentPath}' is no longer a solution document."));

		Solution? changed = await CodeActionService.ComputeChangedSolutionAsync(source.Document, actionRef, cancellationToken);
		if (changed is null)
			return Failure(Error.Conflict("The action is no longer available; the code may have changed. Re-run get_code_actions."));

		try
		{
			changed = await RazorGeneratedChangeFolder.FoldAsync(solution, changed, cancellationToken);
		}
		catch (RazorMappingException exception)
		{
			return Failure(RazorGeneratedChangeFolder.ErrorFor(exception));
		}

		IReadOnlyList<string> files;
		if (checkOnly)
		{
			files = ApplyPipeline.GetChangedFilePaths(solution, changed);
		}
		else
		{
			try
			{
				files = await ApplyPipeline.ApplyAsync(instance, changed, basedOn: solution, cancellationToken);
			}
			catch (StaleWriteException exception)
			{
				return OutlineError.Format(
					Error.Stale(exception.Message, [SolutionRelativePath.Of(solutionDirectory, exception.FilePath) ?? exception.FilePath]),
					instance.CurrentModel.Status);
			}
		}

		var builder = new OutlineBuilder();
		builder.Header("applied", !checkOnly);
		builder.Header("action", actionRef.Key);
		builder.Status(instance.CurrentModel.Status);
		ChangedFilesOutline.Write(builder, files, instance.CurrentSolution, solutionDirectory);
		return builder.ToString();
	}
}
