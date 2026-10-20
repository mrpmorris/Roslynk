using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Morris.Roslynk;
using Morris.Roslynk.Features.MultiQuery;
using static Morris.Roslynk.McpTests.Server.PublishedSchema;

namespace Morris.Roslynk.McpTests.Server;

/// <summary>
/// multi_query's published contract: the batch schema (solutionId + operations + expectSnapshot) and the
/// operations item schema - the tool enum published as JSON string literals, which is the load-bearing
/// surface for callers. Without JsonStringEnumConverter the enum publishes as integers and callers would
/// have to send a numeric tool code.
/// </summary>
public class MultiQuerySchemaTests : IClassFixture<ServerBuilder>
{
	private readonly IReadOnlyList<McpServerTool> Tools;

	public MultiQuerySchemaTests(ServerBuilder server)
	{
		Tools = server.Tools;
	}

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
	public void WhenTheOperationsItemSchemaIsPublished_ThenToolAndArgumentsCarryTheirContracts()
	{
		JsonElement item = Schema()
			.GetProperty("properties").GetProperty("operations")
			.GetProperty("items");

		Assert.Equal("object", item.GetProperty("type").GetString());
		Assert.True(item.GetProperty("properties").TryGetProperty("tool", out JsonElement tool));
		Assert.True(item.GetProperty("properties").TryGetProperty("arguments", out JsonElement arguments));
		Assert.Equal("Read-only tool to run.", tool.GetProperty("description").GetString());
		Assert.Equal("Tool's single-call arguments. Default: null", arguments.GetProperty("description").GetString());
		Assert.False(arguments.TryGetProperty("default", out _), "the default must live in the description, not as a schema keyword.");
	}

	[Fact]
	public async Task WhenAnOperationNamesAToolOutsideTheEnum_ThenTheBinderAcceptsItForPerSlotReporting()
	{
		// The lenient converter accepts an operation outside the enum (and a non-object arguments
		// value) so multi_query can report it in its own slot, naming the index and echoing it, while
		// the rest of the batch runs. The old binder failed the whole call with a CLR type name.
		McpServerTool published = PublishedMultiQuery();

		var requestParams = new CallToolRequestParams
		{
			Name = MultiQueryTool.MultiQueryName,
			Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
			{
				["solutionId"] = JsonSerializer.SerializeToElement("irrelevant - binding must accept the batch"),
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
		Assert.StartsWith("error=", text);
		Assert.DoesNotContain("could not be converted", text, StringComparison.Ordinal);
		Assert.DoesNotContain("MultiQueryOperation", text, StringComparison.Ordinal);
	}

	private JsonElement Schema() =>
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

	private McpServerTool PublishedMultiQuery() =>
		Tools.Single(tool => tool.ProtocolTool.Name == MultiQueryTool.MultiQueryName);
}
