using System.Text.RegularExpressions;
using Morris.Roslynk.Features.References.RenameSymbol;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;
using Morris.Roslynk.Infrastructure.Resolution;
using Morris.Roslynk.Infrastructure.Writing;

namespace Morris.Roslynk.Tests.Features.References.RenameSymbolTests;

public class ConditionalRenameTests
{
	[Fact]
	public async Task WhenRenamingASymbolUsedInBothBranches_ThenBothIfAndElseOccurrencesAreRewritten()
	{
		// A scratch copy, because rename writes to disk; Caller.Run calls Target.Ping in both #if DEBUG and #else.
		string scratch = TestSolutions.CreateScratchConditionalSolution();
		using var registry = new InstanceRegistry();
		await registry.GetOrAddAsync(scratch);
		var subject = new RenameSymbolTool(registry, new SymbolResolver(), new ProjectionService(), new ApplyPipeline());

		string result = await subject.RenameSymbol(scratch, "ConditionalLib.Target.Ping", "Pong");

		Assert.Contains("applied=Y", result);

		string caller = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(scratch)!, "ConditionalLib", "Caller.cs"));
		// Single-projection rename would leave the #else call as 'Ping' (1 occurrence); multi-projection rewrites both.
		Assert.Equal(2, Regex.Matches(caller, @"\bPong\b").Count);
	}
}
