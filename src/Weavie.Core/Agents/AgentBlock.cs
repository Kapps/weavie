using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Agents;

/// <summary>A chart or table an agent shows in its pane and updates in place; it holds only its latest state.</summary>
public sealed record AgentBlock {
	/// <summary>The agent-chosen id, unique within its conversation.</summary>
	public required string Id { get; init; }

	/// <summary>The title shown above the block.</summary>
	public required string Title { get; init; }

	/// <summary>When an update last changed the block, as Unix milliseconds; null until the first update.</summary>
	public long? UpdatedAtMs { get; init; }

	/// <summary>The chart, when the block is one.</summary>
	public AgentChart? Chart { get; init; }

	/// <summary>The table, when the block is one.</summary>
	public AgentTable? Table { get; init; }

	/// <summary>The block's kind: <c>chart</c> or <c>table</c>.</summary>
	[JsonIgnore]
	public string Kind => Chart is null ? "table" : "chart";
}

/// <summary>A chart description: Weavie owns the format and draws it.</summary>
public sealed record AgentChart {
	/// <summary><c>line</c>, <c>area</c>, <c>bar</c>, or <c>scatter</c>.</summary>
	public required string Kind { get; init; }

	/// <summary>The horizontal axis; its <see cref="AgentChartAxis.Type"/> is required.</summary>
	public required AgentChartAxis X { get; init; }

	/// <summary>The left vertical axis.</summary>
	public AgentChartAxis? Y { get; init; }

	/// <summary>The optional right vertical axis.</summary>
	public AgentChartAxis? Y2 { get; init; }

	/// <summary>The plotted series.</summary>
	public required IReadOnlyList<AgentChartSeries> Series { get; init; }

	/// <summary>Horizontal reference lines.</summary>
	public IReadOnlyList<AgentChartThreshold>? Thresholds { get; init; }

	/// <summary>Shaded ranges.</summary>
	public IReadOnlyList<AgentChartBand>? Bands { get; init; }
}

/// <summary>One chart axis.</summary>
public sealed record AgentChartAxis {
	/// <summary>The x axis's scale: <c>time</c>, <c>category</c>, or <c>number</c>; absent on a y axis.</summary>
	public string? Type { get; init; }

	/// <summary>The axis label.</summary>
	public string? Label { get; init; }

	/// <summary>The unit appended to values.</summary>
	public string? Unit { get; init; }
}

/// <summary>One named series.</summary>
public sealed record AgentChartSeries {
	/// <summary>The series name, unique within the chart.</summary>
	public required string Name { get; init; }

	/// <summary><c>solid</c> (default) or <c>comparison</c>, drawn dashed and dim.</summary>
	public string? Style { get; init; }

	/// <summary><c>y</c> (default) or <c>y2</c>.</summary>
	public string? Axis { get; init; }

	/// <summary>The <c>[x, y]</c> points; a time x is stored as Unix milliseconds.</summary>
	public required IReadOnlyList<IReadOnlyList<AgentBlockValue>> Points { get; init; }
}

/// <summary>A horizontal reference line.</summary>
public sealed record AgentChartThreshold {
	/// <summary>The line's label.</summary>
	public string? Label { get; init; }

	/// <summary><c>y</c> (default) or <c>y2</c>.</summary>
	public string? Axis { get; init; }

	/// <summary>The line's value.</summary>
	public required double Value { get; init; }
}

/// <summary>A shaded range along one axis.</summary>
public sealed record AgentChartBand {
	/// <summary>The band's label.</summary>
	public string? Label { get; init; }

	/// <summary><c>x</c> (default), <c>y</c>, or <c>y2</c>.</summary>
	public string? Axis { get; init; }

	/// <summary>Where the band starts.</summary>
	public required AgentBlockValue From { get; init; }

	/// <summary>Where the band ends.</summary>
	public required AgentBlockValue To { get; init; }
}

/// <summary>A table description.</summary>
public sealed record AgentTable {
	/// <summary>The columns, in order.</summary>
	public required IReadOnlyList<AgentTableColumn> Columns { get; init; }

	/// <summary>The rows; each has one value per column.</summary>
	public required IReadOnlyList<IReadOnlyList<AgentBlockValue>> Rows { get; init; }
}

/// <summary>One table column.</summary>
public sealed record AgentTableColumn {
	/// <summary>The column header.</summary>
	public required string Name { get; init; }

	/// <summary><c>time</c>, <c>number</c>, <c>text</c>, or <c>level</c>.</summary>
	public required string Type { get; init; }
}

/// <summary>A JSON scalar in a block: a number, a string, or null.</summary>
[JsonConverter(typeof(AgentBlockValueConverter))]
public readonly record struct AgentBlockValue(double? Number, string? Text) {
	/// <summary>Whether the value is JSON null.</summary>
	public bool IsNull => Number is null && Text is null;

	/// <inheritdoc/>
	public override string ToString() =>
		Number is { } number ? number.ToString(CultureInfo.InvariantCulture) : Text ?? string.Empty;
}

/// <summary>Reads and writes an <see cref="AgentBlockValue"/> as a bare JSON scalar.</summary>
public sealed class AgentBlockValueConverter : JsonConverter<AgentBlockValue> {
	/// <inheritdoc/>
	public override bool HandleNull => true;

	/// <inheritdoc/>
	public override AgentBlockValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch {
		JsonTokenType.Number => new(reader.GetDouble(), null),
		JsonTokenType.String => new(null, reader.GetString()),
		JsonTokenType.Null => default,
		_ => throw new JsonException("A block value must be a number, a string, or null."),
	};

	/// <inheritdoc/>
	public override void Write(Utf8JsonWriter writer, AgentBlockValue value, JsonSerializerOptions options) {
		if (value.Number is { } number) writer.WriteNumberValue(number);
		else if (value.Text is { } text) writer.WriteStringValue(text);
		else writer.WriteNullValue();
	}
}

/// <summary>What an agent's block tool call asks for: a new block (show) or a change to one (update).</summary>
public sealed record AgentBlockRequest {
	/// <summary>The block id.</summary>
	public required string Id { get; init; }

	/// <summary>The title; required to show, optional to update.</summary>
	public string? Title { get; init; }

	/// <summary>A whole chart, to show or to replace the current one.</summary>
	public AgentChart? Chart { get; init; }

	/// <summary>A whole table, to show or to replace the current one.</summary>
	public AgentTable? Table { get; init; }

	/// <summary>Points or rows to add to the current block.</summary>
	public AgentBlockAppend? Append { get; init; }

	/// <summary>How many points per series, or rows, an append keeps.</summary>
	public int? Keep { get; init; }
}

/// <summary>Data appended to a block.</summary>
public sealed record AgentBlockAppend {
	/// <summary>Points to add, by existing series name.</summary>
	public IReadOnlyList<AgentChartSeriesPoints>? Series { get; init; }

	/// <summary>Rows to add to a table.</summary>
	public IReadOnlyList<IReadOnlyList<AgentBlockValue>>? Rows { get; init; }
}

/// <summary>Points appended to one existing series.</summary>
public sealed record AgentChartSeriesPoints {
	/// <summary>The existing series' name.</summary>
	public required string Name { get; init; }

	/// <summary>The <c>[x, y]</c> points to add.</summary>
	public required IReadOnlyList<IReadOnlyList<AgentBlockValue>> Points { get; init; }
}

/// <summary>A validated block tool call, applied when the call's transcript row is claimed.</summary>
public abstract record AgentBlockOperation(string Id) {
	/// <summary>Shows a new block.</summary>
	public sealed record Show(AgentBlock Block) : AgentBlockOperation(Block.Id);

	/// <summary>Changes an existing block.</summary>
	public sealed record Update(AgentBlockRequest Request) : AgentBlockOperation(Request.Id);
}

/// <summary>A block and the provider and Weavie conversation that own it.</summary>
public sealed record AgentBlockState(string ProviderId, string? ConversationId, AgentBlock Block);

/// <summary>A block's latest state at the output revision that last changed it.</summary>
public sealed record AgentBlockRecord(long Revision, AgentBlockState State);

/// <summary>Source-generated block contracts, shared by the tool server and the conversation store.</summary>
[JsonSourceGenerationOptions(
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
	NumberHandling = JsonNumberHandling.AllowReadingFromString,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentBlockRequest))]
[JsonSerializable(typeof(AgentBlock))]
public sealed partial class AgentBlockJson : JsonSerializerContext;
