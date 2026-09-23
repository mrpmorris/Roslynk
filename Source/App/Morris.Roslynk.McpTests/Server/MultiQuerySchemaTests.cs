using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk;
using Morris.Roslynk.Features.MultiQuery;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// multi_query's published contract: the batch schema (solutionId + operations + expectSnapshot) and the
/// operations item schema - the tool enum published as JSON string literals, which is the load-bearing
/// surface for callers. Without JsonStringEnumConverter the enum publishes as integers and callers would
/// have to send a numeric tool code.
/// </summary>
public class MultiQuerySchemaTests
{
	[Fact]
	public void WhenMultiQueryIsPublished_ThenItsSchemaRequiresSolutionIdAndOperations()
	{
		JsonElement schema = Schema();

		Assert.Equal(
			new[] { "operations", "solutionId" }.OrderBy(name => name, StringComparer.Ordinal),
			Required(schema).OrderBy(name => name, StringComparer.Ordinal));
		Assert.True(Properties(schema).ContainsKey("expectSnapshot"), "expectSnapshot (optional) is missing from the schema.");
		Assert.False(Required(schema).Contains("expectSnapshot"), "expectSnapshot must be omittable.");
	}

	[Fact]
	public void WhenTheOperationsItemSchemaIsPublished_ThenTheToolIsAStringEnumOfTheElevenNames()
	{
		JsonElement tool = Schema()
			.GetProperty("properties").GetProperty("operations")
			.GetProperty("items").GetProperty("properties").GetProperty("tool");

		Assert.True(
			tool.TryGetProperty("enum", out JsonElement enumValues),
			$"operations.items.tool must publish an enum; actual: {tool.GetRawText()}");

		string[] names = enumValues.EnumerateArray().Select(value => value.GetString()!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
		string[] expected = Enum.GetNames<MultiQueryOp>().OrderBy(name => name, StringComparer.Ordinal).ToArray();
		Assert.Equal(expected, names);
		Assert.Contains("get_symbol", names);
	}

	[Fact]
	public async Task WhenAnOperationNamesAToolOutsideTheEnum_ThenTheWholeCallIsInvalid()
	{
		// Decision 4: a write tool is unrepresentable in the enum, so the SDK's binding layer refuses the
		// call before multi_query runs, and RoslynkTool renders the standard header-only Invalid. This is
		// the only place that behaviour is observable - constructing the tool directly cannot reach it.
		McpServerTool published = PublishedMultiQuery();

		var requestParams = new CallToolRequestParams
		{
			Name = MultiQueryTool.MultiQueryName,
			Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["solutionId"] = JsonSerializer.SerializeToElement("irrelevant - refused at binding"),
				["operations"] = JsonSerializer.SerializeToElement(
					new Dictionary<string, JsonElement>[] { new() { ["tool"] = JsonSerializer.SerializeToElement("rename_symbol") } }),
			},
		};
		var request = new JsonRpcRequest
		{
			Id = new RequestId(1),
			Method = RequestMethods.ToolsCall,
			Params = JsonSerializer.SerializeToNode(requestParams),
		};
		var context = new RequestContext<CallToolRequestParams>(Server(), request, requestParams);

		CallToolResult result = await published.InvokeAsync(context);

		string text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
		Assert.True(result.IsError);
		Assert.Contains("error=Invalid", text, StringComparison.Ordinal);
		Assert.DoesNotContain("operations=", text, StringComparison.Ordinal);
	}

	private static JsonElement Schema() =>
		PublishedMultiQuery().ProtocolTool.InputSchema;

	/// <summary>
	/// A real in-proc McpServer (the RequestContext needs one; the binding layer runs inside the server).
	/// The server session is never connected - the transport endpoints are unused pipes for this one call.
	/// </summary>
	private static McpServer Server()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
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

	private static McpServerTool PublishedMultiQuery() =>
		BuildServer().Single(tool => tool.ProtocolTool.Name == MultiQueryTool.MultiQueryName);

	private static IReadOnlyList<McpServerTool> BuildServer()
	{
		var services = new ServiceCollection();
		services.AddRoslynk();
		services.AddMcpServer().WithRoslynkTools();
		using ServiceProvider provider = services.BuildServiceProvider();
		return provider.GetServices<McpServerTool>().ToList();
	}

	private static IReadOnlyDictionary<string, JsonElement> Properties(JsonElement schema) =>
		schema.TryGetProperty("properties", out JsonElement properties)
			? properties.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal)
			: new Dictionary<string, JsonElement>(StringComparer.Ordinal);

	private static IReadOnlyCollection<string> Required(JsonElement schema) =>
		schema.TryGetProperty("required", out JsonElement required)
			? required.EnumerateArray().Select(value => value.GetString()!).ToArray()
			: [];
}
