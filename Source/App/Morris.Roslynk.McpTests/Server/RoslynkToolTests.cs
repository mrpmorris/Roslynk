using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk.Infrastructure.Outlines;
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
			([Description("Maximum results to return.")] int maxSize = 50) => maxSize,
			new McpServerToolCreateOptions { Name = "sample" });

		var tool = new RoslynkTool(inner);

		string description = tool.ProtocolTool.InputSchema
			.GetProperty("properties").GetProperty("maxSize").GetProperty("description").GetString()!;

		// The default is generated from the C# default value, so the text cannot drift from the signature.
		Assert.Contains("Maximum results to return.", description, StringComparison.Ordinal);
		Assert.Equal("Maximum results to return. Default: 50", description);
	}

	[Fact]
	public void WhenDefaultsContainSuffixText_ThenValuesAndAuthoredDescriptionsArePreserved()
	{
		McpServerTool inner = McpServerTool.Create(
			([Description("(Default value: untouched)")] string required,
				string label = "(Default value: example)", bool enabled = false, string? optional = null) => required,
			new McpServerToolCreateOptions { Name = "sample" });
		var tool = new RoslynkTool(inner);
		JsonElement properties = tool.ProtocolTool.InputSchema.GetProperty("properties");

		Assert.Equal("(Default value: untouched)", properties.GetProperty("required").GetProperty("description").GetString());
		string label = properties.GetProperty("label").GetProperty("description").GetString()!;
		Assert.StartsWith("Default: ", label, StringComparison.Ordinal);
		Assert.Contains("(Default value: example)", label, StringComparison.Ordinal);
		Assert.Equal("Default: false", properties.GetProperty("enabled").GetProperty("description").GetString());
		Assert.Equal("Default: null", properties.GetProperty("optional").GetProperty("description").GetString());
		Assert.DoesNotContain("\"default\"", tool.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
		Assert.Contains("\"default\"", inner.ProtocolTool.InputSchema.GetRawText(), StringComparison.Ordinal);
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

	[Fact]
	public async Task WhenAResultExceedsTheBudget_ThenTheWrapperCutsItAndMarksOutputTruncated()
	{
		// get_members on a huge type is the realistic case; a stub stands in for any tool that cannot page
		// yet. The cut is the wrapper's last resort, so it goes through the full published path.
		McpServerTool inner = McpServerTool.Create(
			(int chars) => new string('x', chars) + "\n",
			new McpServerToolCreateOptions { Name = "oversized" });

		var wrapped = new RoslynkTool(inner, new ResponseBudget(80_000));
		CallToolResult result = await InvokeAsync(wrapped, "oversized", ("chars", JsonSerializer.SerializeToElement(300_000)));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.StartsWith("outputTruncated=Y\n", text, StringComparison.Ordinal);
		Assert.Contains("fullOutputChars=300001\n", text, StringComparison.Ordinal);
		Assert.True(text.Length <= 80_000, $"Cut result was {text.Length} chars.");
	}

	[Fact]
	public async Task WhenAResultFits_ThenTheWrapperLeavesItUntouched()
	{
		McpServerTool inner = McpServerTool.Create(
			(int chars) => new string('x', chars),
			new McpServerToolCreateOptions { Name = "fits" });

		var wrapped = new RoslynkTool(inner, new ResponseBudget(80_000));
		CallToolResult result = await InvokeAsync(wrapped, "fits", ("chars", JsonSerializer.SerializeToElement(100)));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.Equal(new string('x', 100), text);
	}

	[Fact]
	public async Task WhenAnUnknownArgumentIsSupplied_ThenTheToolIsNotInvokedAndTheCallIsUnknownArgument()
	{
		bool invoked = false;
		McpServerTool inner = McpServerTool.Create(
			(string name, int take = 10) =>
			{
				invoked = true;
				return name;
			},
			new McpServerToolCreateOptions { Name = "sample" });

		var wrapped = new RoslynkTool(inner);
		CallToolResult result = await InvokeAsync(
			wrapped,
			"sample",
			("name", JsonSerializer.SerializeToElement("x")),
			("documentPath", JsonSerializer.SerializeToElement("ignored")));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.True(result.IsError);
		Assert.StartsWith("error=UnknownArgument\n", text, StringComparison.Ordinal);
		Assert.Contains("errorMessage=Unknown argument 'documentPath' for sample.", text, StringComparison.Ordinal);
		Assert.Contains("Valid: name, take", text, StringComparison.Ordinal);
		Assert.False(invoked);
	}

	[Fact]
	public async Task WhenAnUnknownArgumentLooksLikeAValidOne_ThenTheMessageSuggestsIt()
	{
		McpServerTool inner = McpServerTool.Create(
			(string filePath) => filePath,
			new McpServerToolCreateOptions { Name = "diagnostics" });

		var wrapped = new RoslynkTool(inner);
		CallToolResult result = await InvokeAsync(
			wrapped,
			"diagnostics",
			("filePth", JsonSerializer.SerializeToElement("a.cs")));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.Contains("Did you mean 'filePath'?", text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task WhenAToolTakesNoArguments_ThenAnyArgumentIsRejected()
	{
		McpServerTool inner = McpServerTool.Create(
			() => "ok",
			new McpServerToolCreateOptions { Name = "noargs" });

		var wrapped = new RoslynkTool(inner);
		CallToolResult result = await InvokeAsync(wrapped, "noargs", ("extra", JsonSerializer.SerializeToElement(1)));

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.StartsWith("error=UnknownArgument\n", text, StringComparison.Ordinal);
		Assert.Contains("The tool takes no arguments.", text, StringComparison.Ordinal);
	}

	/// <summary>Invokes a wrapped tool through the real binding layer, exactly as a client call arrives.</summary>
	private static async Task<CallToolResult> InvokeAsync(RoslynkTool tool, string name, params (string Key, JsonElement Value)[] arguments)
	{
		var requestParams = new CallToolRequestParams
		{
			Name = name,
			Arguments = arguments.ToDictionary(
				pair => pair.Key,
				pair => pair.Value),
		};
		var request = new JsonRpcRequest
		{
			Id = new RequestId(1),
			Method = RequestMethods.ToolsCall,
			Params = JsonSerializer.SerializeToNode(requestParams),
		};
		var context = new RequestContext<CallToolRequestParams>(Server(), request, requestParams);
		return await tool.InvokeAsync(context);
	}

	/// <summary>
	/// A real in-proc McpServer (the RequestContext needs one; the binding layer runs inside the server).
	/// The server session is never connected - the transport endpoints are unused pipes for this one call.
	/// </summary>
	private static McpServer Server()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		ServiceProvider provider = services.BuildServiceProvider();

		var transport = new NullTransport();
		return McpServer.Create(transport, new McpServerOptions(), provider.GetRequiredService<ILoggerFactory>(), provider);
	}

	private sealed class NullTransport : ITransport
	{
		public string? SessionId => null;
		public bool IsConnected => false;
		public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => null!;
		public Task<string?> StartListeningAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
		public Task StopListeningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
		public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
		public ValueTask DisposeAsync() => default;
	}
}
