using System.Text;
using System.Text.Json;

namespace Morris.Roslynk.Mcp.Hosting;

/// <summary>
/// Replaces the MCP transport's generic "not a valid JSON-RPC message" rejection with the JSON parser's
/// own diagnosis: the failing line, byte offset, offending token/escape and a hint. The transport only
/// knows that the body did not parse; the parser knows exactly where and why, and discarding that turned
/// a one-line fix (a Windows path with an unescaped backslash) into repeated blind retries.
/// </summary>
/// <remarks>
/// Only POSTs to the MCP route are inspected, and only when the body is non-empty. The body is buffered
/// with <see cref="HttpRequest.EnableBuffering()"/> and rewound, so a valid request - including a chunked
/// or streaming one - reaches the transport exactly as it arrived. Everything else, including the
/// daemon's own endpoints, passes straight through.
/// </remarks>
public static class McpBodyValidationExtensions
{
	/// <summary>The route <c>MapMcp()</c> maps by default; the only path this middleware inspects.</summary>
	public const string McpPath = "/";

	/// <summary>
	/// Adds the JSON-body diagnosis middleware. Call it after routing and before <c>MapMcp()</c> so a
	/// malformed body is answered here instead of by the transport.
	/// </summary>
	public static IApplicationBuilder UseMcpBodyValidation(this IApplicationBuilder app)
	{
		ArgumentNullException.ThrowIfNull(app);
		return app.Use(ValidateMcpBodyAsync);
	}

	private static async Task ValidateMcpBodyAsync(HttpContext context, RequestDelegate next)
	{
		if (!HttpMethods.IsPost(context.Request.Method) || context.Request.Path != McpPath)
		{
			await next(context);
			return;
		}

		// EnableBuffering makes the read replayable: the transport reads the same stream afterwards,
		// so a valid body is never consumed by this check.
		context.Request.EnableBuffering();
		string body;
		using (var reader = new StreamReader(
			context.Request.Body,
			Encoding.UTF8,
			detectEncodingFromByteOrderMarks: false,
			bufferSize: 16 * 1024,
			leaveOpen: true))
		{
			body = await reader.ReadToEndAsync(context.RequestAborted);
		}
		context.Request.Body.Position = 0;

		// An empty body is a framing problem, not a JSON parse failure; the transport's -32600 is right.
		if (string.IsNullOrWhiteSpace(body))
		{
			await next(context);
			return;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
		}
		catch (JsonException exception)
		{
			await WriteParseErrorAsync(context, body, exception);
			return;
		}

		await next(context);
	}

	private static async Task WriteParseErrorAsync(HttpContext context, string body, JsonException exception)
	{
		long line = exception.LineNumber ?? 0;
		long bytePosition = exception.BytePositionInLine ?? 0;
		long offset = AbsoluteByteOffset(body, line, bytePosition);

		string message = $"Body is not valid JSON at line {line + 1}, offset {offset}: {exception.Message}";
		if (exception.Message.Contains("escap", StringComparison.OrdinalIgnoreCase))
		{
			message +=
				" Hint: in a JSON string a backslash starts an escape sequence, so a Windows path must be written with doubled backslashes (C:\\\\Data) or with forward slashes (C:/Data).";
		}
		else
		{
			message += " Hint: the POST body must be one JSON-RPC message object with jsonrpc, id and method.";
		}

		context.Response.StatusCode = StatusCodes.Status400BadRequest;
		context.Response.ContentType = "application/json; charset=utf-8";
		string payload = JsonSerializer.Serialize(new
		{
			jsonrpc = "2.0",
			id = (string?)null,
			error = new
			{
				code = -32700,
				message,
			},
		});
		await context.Response.WriteAsync(payload, context.RequestAborted);
	}

	/// <summary>
	/// The absolute byte offset of the parser's line-relative position. Byte offsets are what the parser
	/// reports; converting through UTF-8 keeps the number meaningful for non-ASCII bodies too.
	/// </summary>
	private static long AbsoluteByteOffset(string body, long line, long bytePositionInLine)
	{
		string[] lines = body.Split('\n');
		long offset = 0;
		for (int index = 0; index < line && index < lines.Length; index++)
			offset += Encoding.UTF8.GetByteCount(lines[index]) + 1; // + the '\n' separator
		return offset + bytePositionInLine;
	}
}
