using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Tools;

namespace Morris.Roslynk.McpTests.Server;

public class RoslynkToolTests
{
	[Fact]
	public void WhenAToolIsWrapped_ThenTheDefaultKeywordIsStrippedFromItsSchema()
	{
		McpServerTool inner = McpServerTool.Create(
			(string name, int take = 10) => name,
			new McpServerToolCreateOptions { Name = "sample" });

		Assert.Contains("\"default\"", inner.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);

		var tool = new RoslynkTool(inner);

		Assert.DoesNotContain("\"default\"", tool.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
		Assert.Equal("sample", tool.ProtocolTool.Name);
		// The parameter is still declared, and still omittable.
		Assert.True(tool.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("take", out _));
		Assert.DoesNotContain(
			"take",
			tool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
	}

	[Fact]
	public void WhenAToolIsWrapped_ThenTheOriginalSchemaIsLeftUntouched()
	{
		McpServerTool inner = McpServerTool.Create(
			(int take = 10) => take,
			new McpServerToolCreateOptions { Name = "sample" });

		_ = new RoslynkTool(inner);

		Assert.Contains("\"default\"", inner.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
	}

	[Fact]
	public void WhenADefaultIsMovedIntoTheDescription_ThenTheCallerCanStillSeeIt()
	{
		McpServerTool inner = McpServerTool.Create(
			([Description("Maximum results to return.")] int maxResults = 50) => maxResults,
			new McpServerToolCreateOptions { Name = "sample" });

		var tool = new RoslynkTool(inner);

		string description = tool.ProtocolTool.InputSchema
			.GetProperty("properties").GetProperty("maxResults").GetProperty("description").GetString()!;

		// The default is generated from the C# default value, so the text cannot drift from the signature.
		Assert.Contains("Maximum results to return.", description, StringComparison.Ordinal);
		Assert.Contains("50", description, StringComparison.Ordinal);
	}

	[Fact]
	public void WhenAnArgumentCannotBeBound_ThenTheResultIsAStructuredInvalidError()
	{
		CallToolResult result = RoslynkTool.ToErrorResult(new FormatException("'abc' is not a number."));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.True(result.IsError);
		Assert.Contains("error=Invalid", text, StringComparison.Ordinal);
		Assert.Contains("errorMessage='abc' is not a number.", text, StringComparison.Ordinal);
	}

	[Fact]
	public void WhenAToolFaults_ThenTheResultSaysSo()
	{
		CallToolResult result = RoslynkTool.ToErrorResult(new InvalidOperationException("The workspace is gone."));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.True(result.IsError);
		Assert.Contains("error=Faulted", text, StringComparison.Ordinal);
	}
}
