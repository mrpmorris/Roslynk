using System.ComponentModel;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Tools;
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
public sealed class ApplyCodeActionTool : IMcpTool
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

	public static string GetName() => ApplyCodeActionName;

	public static string GetDescription() => """
		Applies actionId from get_code_actions, re-resolving at its original position; retains no action state.

		Errors: Conflict if the action no longer resolves (rerun get_code_actions); Invalid for a
		malformed actionId. Adding usings/members to Razor-generated
		scaffolding may be unmappable.

		Additional header: action.
		""";

	[McpServerTool(
		Name = ApplyCodeActionName,
		Title = "Apply a code action",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	public async Task<string> ApplyCodeAction(
		[Description(ParameterDescriptions.SolutionId)] string solutionId,
		[Description("actionId from get_code_actions.")] string actionId,
		[Description(ParameterDescriptions.CheckOnly)] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrBeginAsync(solutionId, cancellationToken);
		SolutionModel model = instance.CurrentModel;

		string Failure(Error error) => OutlineError.Format(error, model.Status);

		if (model.Solution is null)
			return Failure(Error.Indexing(instance.LoadedProjects));

		if (!CodeActionService.TryDecodeId(actionId, out ActionRef actionRef))
			return Failure(Error.Invalid("The actionId could not be decoded; get a fresh one from get_code_actions."));

		Solution solution = model.Solution;
		string? solutionDirectory = SolutionRelativePath.DirectoryOf(solution);

		(RazorSourceDocument? source, Error? pathError) = await RazorSourceDocument.ResolveWithErrorAsync(
			solution,
			actionRef.DocumentPath,
			$"'{actionRef.DocumentPath}' is no longer a solution document.",
			cancellationToken);
		if (pathError is Error error)
			return Failure(error);
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
