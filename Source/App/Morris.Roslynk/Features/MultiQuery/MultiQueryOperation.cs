using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Morris.Roslynk.Features.MultiQuery;

/// <summary>
/// The tools a multi_query operation may invoke: exactly the read-only query tools that run purely on the
/// pinned solution snapshot. The enum is the contract, not a convenience - the published schema advertises
/// these names, so an operation naming anything else (a write tool, get_diagnostics, an unknown name) is
/// refused. That refusal is reported in the operation's own slot (see <see cref="MultiQueryOperation"/>),
/// naming the index and the offending value, instead of failing the whole batch at argument binding.
/// Member names are spelled exactly like each tool's 'public const string <Tool>Name'; the catalog
/// resolution test keeps the two in lockstep.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultiQueryOp
{
	get_symbol,
	get_symbol_body,
	get_members,
	find_definition,
	find_implementations,
	find_references,
	get_callers,
	get_callees,
	search_symbols,
	get_type_hierarchy,
	find_dead_code,
	find_dead_conditionals,
	get_expression_info,
	find_reads,
	find_writes,
}

/// <summary>
/// One operation inside a multi_query batch: which tool to run, plus its arguments. The arguments use
/// exactly that tool's single-call parameter names - anything else is rejected rather than ignored, so a
/// misspelled parameter can never silently fall back to its default. 'solutionId' is deliberately not an
/// argument: the batch carries it once, and every operation runs against the snapshot pinned from it.
/// </summary>
/// <remarks>
/// Binding is deliberately lenient: <see cref="MultiQueryOperationConverter"/> records an uninterpretable
/// operation in <see cref="BindingError"/> instead of throwing, so the batch still runs and that one slot
/// reports Invalid naming its index and echoing the operation. Only a batch whose operations array itself
/// cannot be read fails at binding.
/// </remarks>
[JsonConverter(typeof(MultiQueryOperationConverter))]
public sealed record MultiQueryOperation(
	[Description("Read-only tool to run.")]
	MultiQueryOp Tool,
	[Description("Tool's single-call arguments.")]
	IReadOnlyDictionary<string, JsonElement>? Arguments = null)
{
	/// <summary>
	/// Why the caller's JSON could not be interpreted as an operation, or null when it bound cleanly.
	/// A non-null value makes this slot error=Invalid and never reaches the catalog.
	/// </summary>
	[JsonIgnore]
	internal string? BindingError { get; init; }

	/// <summary>The operation exactly as the caller sent it, echoed in the slot's Invalid message so the
	/// caller can diff it against the tool contract without re-reading the request.</summary>
	[JsonIgnore]
	internal string RawJson { get; init; } = "";

	/// <summary>The tool name as written, including one outside the enum. Empty when constructed in code.</summary>
	[JsonIgnore]
	internal string ToolText { get; init; } = "";

	/// <summary>The name shown in the slot frame: the caller's spelling when it was readable, otherwise
	/// the enum value (direct construction) or the raw text.</summary>
	internal string DisplayTool => ToolText.Length > 0 ? ToolText : Tool.ToString();
}

/// <summary>
/// Reads one operation without ever throwing on a malformed operation: an unknown tool name, a missing
/// tool, or non-object arguments become <see cref="MultiQueryOperation.BindingError"/> so multi_query can
/// report that operation's own slot instead of the SDK's binder failing the whole call with a type name.
/// </summary>
public sealed class MultiQueryOperationConverter : JsonConverter<MultiQueryOperation>
{
	private static readonly string[] ValidTools = Enum.GetNames<MultiQueryOp>();
	private static readonly string ValidToolList = string.Join(", ", ValidTools);

	public override MultiQueryOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		using JsonDocument document = JsonDocument.ParseValue(ref reader);
		JsonElement root = document.RootElement;
		string raw = root.GetRawText();

		if (root.ValueKind != JsonValueKind.Object)
			return Unbound(raw, $"an operation must be a JSON object, got {Describe(root)}.");

		string? toolText = null;
		string? error = null;

		if (!root.TryGetProperty("tool", out JsonElement tool))
		{
			error = "missing required 'tool'.";
		}
		else if (tool.ValueKind != JsonValueKind.String)
		{
			error = $"'tool' must be a string naming a read-only tool, got {tool.GetRawText()}.";
		}
		else
		{
			toolText = tool.GetString() ?? "";
			if (Array.IndexOf(ValidTools, toolText) < 0)
				error = $"'{toolText}' is not a multi-queryable tool. Valid: {ValidToolList}.";
		}

		IReadOnlyDictionary<string, JsonElement>? arguments = null;
		if (error is null && root.TryGetProperty("arguments", out JsonElement argumentsElement)
			&& argumentsElement.ValueKind != JsonValueKind.Null)
		{
			if (argumentsElement.ValueKind != JsonValueKind.Object)
			{
				error = $"'arguments' must be a JSON object mapping argument names to values, got {Describe(argumentsElement)}.";
			}
			else
			{
				var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
				foreach (JsonProperty property in argumentsElement.EnumerateObject())
					values[property.Name] = property.Value.Clone();
				arguments = values;
			}
		}

		MultiQueryOp parsed = error is null ? (MultiQueryOp)Array.IndexOf(ValidTools, toolText!) : default;
		return new MultiQueryOperation(parsed, arguments)
		{
			BindingError = error,
			RawJson = raw,
			ToolText = toolText ?? "",
		};
	}

	public override void Write(Utf8JsonWriter writer, MultiQueryOperation value, JsonSerializerOptions options)
	{
		writer.WriteStartObject();
		writer.WriteString("tool", value.Tool.ToString());
		if (value.Arguments is not null)
		{
			writer.WritePropertyName("arguments");
			writer.WriteStartObject();
			foreach (KeyValuePair<string, JsonElement> argument in value.Arguments)
			{
				writer.WritePropertyName(argument.Key);
				argument.Value.WriteTo(writer);
			}
			writer.WriteEndObject();
		}
		writer.WriteEndObject();
	}

	private static MultiQueryOperation Unbound(string raw, string error) =>
		new(default, null)
		{
			BindingError = error,
			RawJson = raw,
		};

	private static string Describe(JsonElement value) => value.ValueKind switch
	{
		JsonValueKind.Array => "an array",
		JsonValueKind.String => "a string",
		JsonValueKind.Number => "a number",
		JsonValueKind.True or JsonValueKind.False => "a boolean",
		JsonValueKind.Null => "null",
		_ => "an object",
	};
}
