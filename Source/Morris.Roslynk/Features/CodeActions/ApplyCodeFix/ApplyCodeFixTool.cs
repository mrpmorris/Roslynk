using System.ComponentModel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;
using Morris.Roslynk.Features.CodeActions.ApplyCodeAction;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Features.CodeActions.ApplyCodeFix;

[McpServerToolType]
public sealed class ApplyCodeFixTool
{
	public const string ApplyCodeFixName = "apply_code_fix";

	private readonly InstanceRegistry InstanceRegistry;
	private readonly CodeActionService CodeActionService;
	private readonly ApplyPipeline ApplyPipeline;

	public ApplyCodeFixTool(InstanceRegistry instanceRegistry, CodeActionService codeActionService, ApplyPipeline applyPipeline)
	{
		InstanceRegistry = instanceRegistry ?? throw new ArgumentNullException(nameof(instanceRegistry));
		CodeActionService = codeActionService ?? throw new ArgumentNullException(nameof(codeActionService));
		ApplyPipeline = applyPipeline ?? throw new ArgumentNullException(nameof(applyPipeline));
	}

	[McpServerTool(
		Name = ApplyCodeFixName,
		Title = "Apply a diagnostic's fix",
		ReadOnly = false,
		Idempotent = false,
		Destructive = true,
		OpenWorld = false)]
	[Description(
		"""
		Applies the code fix for the first occurrence of a diagnostic id (e.g. CS0219) in a .cs file — the
		quick path when you already know which diagnostic to clear, without first listing actions. Written
		atomically through the same safe write path. Pass checkOnly to preview without writing.
		""")]
	public async Task<ApplyCodeActionResponse> ApplyCodeFix(
		[Description("Solution handle returned by open_solution.")] string solutionId,
		[Description("Path of the .cs file.")] string documentPath,
		[Description("The compiler diagnostic id to fix, e.g. CS0219.")] string diagnosticId,
		[Description("If true, returns the files that would change without writing anything.")] bool checkOnly = false,
		CancellationToken cancellationToken = default)
	{
		RoslynInstance instance = await InstanceRegistry.GetOrAddAsync(solutionId);
		Solution solution = instance.CurrentSolution;

		Document? document = CodeActionService.FindDocument(solution, documentPath);
		if (document is null)
			return new ApplyCodeActionResponse(Applied: false, [], null, $"'{documentPath}' is not a solution-compiled .cs document.");

		Compilation? compilation = await document.Project.GetCompilationAsync(cancellationToken);
		SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
		Diagnostic? diagnostic = compilation?.GetDiagnostics(cancellationToken)
			.FirstOrDefault(candidate => candidate.Id == diagnosticId && candidate.Location.SourceTree == tree);
		if (diagnostic is null)
			return new ApplyCodeActionResponse(Applied: false, [], null, $"No {diagnosticId} diagnostic was found in '{documentPath}'.");

		TextSpan span = diagnostic.Location.SourceSpan;
		IReadOnlyList<DiscoveredAction> actions = await CodeActionService.DiscoverAsync(document, span, cancellationToken);
		DiscoveredAction? fix = actions.FirstOrDefault(action => action.DiagnosticId == diagnosticId);
		if (fix is null)
			return new ApplyCodeActionResponse(Applied: false, [], null, $"No fix is available for {diagnosticId}.");

		Solution? changed = await CodeActionService.ChangedSolutionAsync(fix.Action, cancellationToken);
		if (changed is null)
			return new ApplyCodeActionResponse(Applied: false, [], fix.Action.Title, "The fix produced no changes.");

		if (checkOnly)
			return new ApplyCodeActionResponse(Applied: false, ApplyPipeline.GetChangedFilePaths(solution, changed), fix.Action.Title, "Preview only; nothing was written.");

		IReadOnlyList<string> files = await ApplyPipeline.ApplyAsync(instance, changed, cancellationToken);
		return new ApplyCodeActionResponse(Applied: true, files, fix.Action.Title, Message: null);
	}
}
