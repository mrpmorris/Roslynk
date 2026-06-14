using Morris.Roslynk.Infrastructure.Provenance;

namespace Morris.Roslynk.Tests.Infrastructure.Provenance.SourceSpanTests;

public class LengthTests
{
	[Fact]
	public void WhenSpanHasStartAndEndChars_ThenLengthIsTheExclusiveDifference()
	{
		var subject = new SourceSpan(
			sourcePath: @"C:\Solution\Foo.cs",
			sourceType: SourceType.Source,
			documentVersion: 17,
			startChar: 1200,
			endChar: 1450,
			startLine: 42,
			startColumn: 9,
			endLine: 50,
			endColumn: 5);

		Assert.Equal(250, subject.Length);
	}
}
