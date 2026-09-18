using Morris.Roslynk.Features.CodeActions.ApplyCodeFix;
using Morris.Roslynk.Features.Usings.RemoveUnusedUsings;
using Morris.Roslynk.Infrastructure.CodeActions;
using Morris.Roslynk.Infrastructure.Diagnostics;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests;

/// <summary>
/// Composes the code-action services the way <c>AddRoslynk</c> does, so tests share one
/// <see cref="DocumentDiagnosticsProvider"/> between a tool and the service it calls, as the server does.
/// </summary>
internal static class TestServices
{
	public static CodeActionService CodeActions() => CodeActions(new DocumentDiagnosticsProvider());

	public static CodeActionService CodeActions(DocumentDiagnosticsProvider documentDiagnostics) => new(documentDiagnostics);

	public static ApplyCodeFixTool ApplyCodeFix(InstanceRegistry registry)
	{
		var documentDiagnostics = new DocumentDiagnosticsProvider();
		return new ApplyCodeFixTool(registry, CodeActions(documentDiagnostics), new ApplyPipeline(), documentDiagnostics);
	}

	public static RemoveUnusedUsingsTool RemoveUnusedUsings(InstanceRegistry registry)
	{
		var documentDiagnostics = new DocumentDiagnosticsProvider();
		return new RemoveUnusedUsingsTool(registry, new ApplyPipeline(), CodeActions(documentDiagnostics), documentDiagnostics);
	}
}
