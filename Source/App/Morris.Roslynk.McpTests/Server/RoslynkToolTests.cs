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

	[Fact]
	public async Task WhenAResultExceedsTheBudget_ThenTheWrapperCutsItAndMarksOutputTruncated()
	{
		// get_members on a huge type is the realistic case; a stub stands in for any tool that cannot page
		// yet. The cut is the wrapper's last resort, so it goes through the full published path.
		McpServerTool inner = McpServerTool.Create(
			(int chars) => new string('x', chars) + "\n",
			new McpServerToolCreateOptions { Name = "oversized" });

		var wrapped = new RoslynkTool(inner, new ResponseBudget(80_000));
		CallToolResult result = await InvokeAsync(wrapped, "oversized", [("chars", 300_000)]);

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
		CallToolResult result = await InvokeAsync(wrapped, "fits", [("chars", 100)]);

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.Equal(new string('x', 100), text);
	}

	/// <summary>Invokes a wrapped tool through the real binding layer, exactly as a client call arrives.</summary>
	private static async Task<CallToolResult> InvokeAsync(RoslynkTool tool, string name, params (string Key, int Value)[] arguments)
	{
		var requestParams = new CallToolRequestParams
		{
			Name = name,
			Arguments = arguments.ToDictionary(
				pair => pair.Key,
				pair => JsonSerializer.SerializeToElement(pair.Value)),
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
