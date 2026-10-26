using Morris.Roslynk.Infrastructure.Observability;
using System.Diagnostics;

namespace Morris.Roslynk.Tests.Infrastructure.Observability;

public class ActivityTagsTests
{
	[Fact]
	public void WhenATagIsRecorded_ThenTheWholeValueIsKept()
	{
		string value = new('a', 10_000);

		Activity activity = new(nameof(ActivityTagsTests));
		activity.SetTag(ActivityTags.SolutionPathTag, value);

		Assert.Equal(value, activity.GetTagItem(ActivityTags.SolutionPathTag));
	}
}
