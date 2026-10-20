using System.Buffers;
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
/// or streaming one - reaches the transport exactly as it arrived. Validation reads the raw UTF-8 bytes
/// once and walks them with <see cref="Utf8JsonReader"/>: no decoded string and no document tree are
/// built, so a valid body pays only for the single streaming pass. Everything else, including the
/// daemon's own endpoints, passes straight through.
/// </remarks>
public static class McpBodyValidationExtensions
{
	/// <summary>The route <c>MapMcp()</c> maps by default; the only path this middleware inspects.</summary>
	public const string McpPath = "/";

	private const int InitialBufferSize = 16 * 1024;

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
		byte[]? buffer = null;
		try
		{
			(buffer, int length) = await ReadBodyAsync(context.Request.Body, context.RequestAborted);
			context.Request.Body.Position = 0;

			ReadOnlySpan<byte> body = buffer.AsSpan(0, length);

			// An empty body is a framing problem, not a JSON parse failure; the transport's -32600 is right.
			if (IsWhitespace(body))
			{
				await next(context);
				return;
			}

			try
			{
				// isFinalBlock: true means the reader also rejects anything but whitespace after the
				// single top-level value, matching JsonDocument.Parse's whole-body semantics.
				var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);
				while (reader.Read())
				{
				}
			}
			catch (JsonException exception)
			{
				long line = exception.LineNumber ?? 0;
				long bytePositionInLine = exception.BytePositionInLine ?? 0;
				long offset = AbsoluteByteOffset(body, line, bytePositionInLine);
				string message = BuildMessage(exception.Message, line + 1, offset);
				await WriteParseErrorAsync(context, message);
				return;
			}

			await next(context);
		}
		finally
		{
			// Rewind before the transport reads, on every path including a parse failure.
			context.Request.Body.Position = 0;
			if (buffer is not null)
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}

	/// <summary>
	/// Reads the buffered body into a pooled array, growing it only when the body outgrows the initial
	/// buffer. The returned array is rented from <see cref="ArrayPool{T}.Shared"/> and must be returned.
	/// </summary>
	private static async Task<(byte[] Buffer, int Length)> ReadBodyAsync(
		Stream body,
		CancellationToken cancellationToken)
	{
		byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
		int length = 0;
		try
		{
			while (true)
			{
				if (length == buffer.Length)
				{
					byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
					Buffer.BlockCopy(buffer, 0, larger, 0, length);
					ArrayPool<byte>.Shared.Return(buffer);
					buffer = larger;
				}

				int read = await body.ReadAsync(buffer.AsMemory(length), cancellationToken);
				if (read == 0)
				{
					return (buffer, length);
				}

				length += read;
			}
		}
		catch
		{
			ArrayPool<byte>.Shared.Return(buffer);
			throw;
		}
	}

	private static bool IsWhitespace(ReadOnlySpan<byte> body)
	{
		foreach (byte value in body)
		{
			if (value is not ((byte)' ' or (byte)'	' or (byte)'\n' or (byte)0x0B or (byte)0x0C or (byte)0x0D))
			{
				return false;
			}
		}

		return true;
	}

	private static async Task WriteParseErrorAsync(HttpContext context, string message)
	{
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

	private static string BuildMessage(string parserMessage, long line, long offset)
	{
		string message = $"Body is not valid JSON at line {line}, offset {offset}: {parserMessage}";
		if (parserMessage.Contains("escap", StringComparison.OrdinalIgnoreCase))
		{
			message +=
				" Hint: in a JSON string a backslash starts an escape sequence, so a Windows path must be written with doubled backslashes (C:\\\\Data) or with forward slashes (C:/Data).";
		}
		else
		{
			message += " Hint: the POST body must be one JSON-RPC message object with jsonrpc, id and method.";
		}

		return message;
	}

	/// <summary>
	/// The absolute byte offset of the parser's line-relative position. Byte offsets are what the parser
	/// reports; walking the raw UTF-8 buffer keeps the number meaningful for non-ASCII bodies too.
	/// </summary>
	private static long AbsoluteByteOffset(ReadOnlySpan<byte> body, long line, long bytePositionInLine)
	{
		long offset = 0;
		for (long currentLine = 0; currentLine < line; currentLine++)
		{
			int newline = body.Slice((int)offset).IndexOf((byte)'\n');
			if (newline < 0)
			{
				break;
			}

			offset += newline + 1; // + the '\n' separator
		}

		return offset + bytePositionInLine;
	}
}
