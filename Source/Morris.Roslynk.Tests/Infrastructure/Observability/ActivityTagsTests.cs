using Morris.Roslynk.Infrastructure.Observability;

namespace Morris.Roslynk.Tests.Infrastructure.Observability;

public class ActivityTagsTests
{
	[Fact]
	public void WhenTheValueIsNull_ThenNullIsReturned()
	{
		string? result = ActivityTags.Truncate(null);

		Assert.Null(result);
	}

	[Fact]
	public void WhenTheValueIsShorterThanTheLimit_ThenItIsReturnedUnchanged()
	{
		string? result = ActivityTags.Truncate("short");

		Assert.Equal("short", result);
	}

	[Fact]
	public void WhenTheValueEqualsTheLimit_ThenItIsReturnedUnchanged()
	{
		string value = new('a', ActivityTags.MaxValueLength);

		string? result = ActivityTags.Truncate(value);

		Assert.Equal(value, result);
	}

	[Fact]
	public void WhenTheValueExceedsTheLimit_ThenItIsCappedAtTheLimit()
	{
		string value = new('a', ActivityTags.MaxValueLength + 10);

		string? result = ActivityTags.Truncate(value);

		Assert.Equal(new string('a', ActivityTags.MaxValueLength), result);
	}
}
