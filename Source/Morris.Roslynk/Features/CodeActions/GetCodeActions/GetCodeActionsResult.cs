using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Results;

namespace Morris.Roslynk.Features.CodeActions.GetCodeActions;

/// <summary>
/// The code actions available at a position. An empty <see cref="Actions"/> list is a valid success
/// (nothing applies there); an unresolved document is carried as a NotFound on
/// <see cref="ResultBase.Error"/>.
/// </summary>
public sealed record GetCodeActionsResult : ResultBase
{
	public GetCodeActionsResult(SolutionModel solutionModel, Error? error, IReadOnlyList<CodeActionDto>? actions)
		: base(solutionModel, error)
	{
		Actions = actions;
	}

	public IReadOnlyList<CodeActionDto>? Actions { get; }
}
