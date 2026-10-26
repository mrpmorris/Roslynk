using Morris.Roslynk.Features.Conditionals.FindDeadConditionals;
using Morris.Roslynk.Infrastructure.Lifecycle;
using Morris.Roslynk.Infrastructure.Projections;

namespace Morris.Roslynk.Tests.Features.Conditionals;

public class FindDeadConditionalsTests
{
	[Fact]
	public async Task WhenAnIfSymbolIsDefinedInNoConfiguration_ThenItsBranchIsFlagged()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Conditional);
		var subject = new FindDeadConditionalsTool(registry, new ConditionalCoverage());

		string result = await subject.FindDeadConditionals(TestSolutions.Conditional);

		Assert.Contains("NEVERDEFINED", result);
		Assert.Contains("deadConditionals=1", result);
	}
	[Fact]
	public async Task WhenBranchesAreReachableUnderDebugOrRelease_ThenTheyAreNotFlagged()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Conditional);
		var subject = new FindDeadConditionalsTool(registry, new ConditionalCoverage());

		string result = await subject.FindDeadConditionals(TestSolutions.Conditional);

		// #if DEBUG is reachable in the Debug config and its #else in the Release config, so neither is flagged.
		Assert.DoesNotContain(",if,DEBUG", result);
	}
	[Fact]
	public async Task WhenCountOnlyIsRequested_ThenTheHeaderAndCountAreReturnedWithoutBranches()
	{
		var registry = await TestSolutions.SharedAsync(TestSolutions.Conditional);
		var subject = new FindDeadConditionalsTool(registry, new ConditionalCoverage());

		string result = await subject.FindDeadConditionals(TestSolutions.Conditional, countOnly: true);

		// The leaves are line:col records, not kinds, so there is no count.<kind>= line.
		OutlineAssert.HeadersAre("deadConditionals=1\ncount=1\n", result);
		Assert.DoesNotContain("NEVERDEFINED", result);
	}
}
