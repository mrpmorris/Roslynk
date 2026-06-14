using Morris.Roslynk.Infrastructure.Provenance;

namespace Morris.Roslynk.Tests.Infrastructure.Provenance.SourceSpanTests;

public class LengthTests
{
	[Fact]
	public void WhenSpanHasStartAndEndChars_ThenLengthIsTheExclusiveDifference()
	{
		var subject = new SourceSpan(
			SourcePath: @"C:\Solution\Foo.cs",
			SourceType: SourceType.Source,
			DocumentVersion: 17,
			StartChar: 1200,
			EndChar: 1450,
			StartLine: 42,
			StartColumn: 9,
			EndLine: 50,
			EndColumn: 5);

		Assert.Equal(250, subject.Length);
	}
}
