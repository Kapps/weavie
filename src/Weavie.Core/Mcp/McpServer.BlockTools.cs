using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weavie.Core.Agents;

namespace Weavie.Core.Mcp;

// showBlock / updateBlock: a chart or table drawn in the native agent pane. See docs/specs/acp-rich-content/design.md.
// The schema is built from the validator's own value sets and limits, so the two can't drift.
public sealed partial class McpServer {
	private const string ShowBlockTool = "showBlock";
	private const string UpdateBlockTool = AgentBlockRules.UpdateTool;

	private static readonly string BlockToolEntries = BlockSchemas();

	private static string BlockSchemas() {
		string limits = string.Create(CultureInfo.InvariantCulture,
			$"{AgentBlockLimits.Series} series of {AgentBlockLimits.Points:N0} points, {AgentBlockLimits.Rows:N0} rows of {AgentBlockLimits.Columns} columns.");
		JsonObject Value() => new() { ["type"] = new JsonArray("number", "string", "null") };
		JsonObject Text() => new() { ["type"] = "string" };
		JsonObject Enum(IReadOnlyList<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(value => (JsonNode)value)]) };
		JsonObject Described(JsonObject schema, string description) {
			schema["description"] = description;
			return schema;
		}
		JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
		JsonObject Object(JsonObject properties, params string[] required) => new() {
			["type"] = "object",
			["properties"] = properties,
			["required"] = new JsonArray([.. required.Select(name => (JsonNode)name)]),
		};
		JsonObject Points() => Described(Array(new JsonObject { ["type"] = "array", ["items"] = Value(), ["minItems"] = 2, ["maxItems"] = 2 }),
			"[x, y] pairs. A time x is Unix milliseconds or ISO 8601.");
		JsonObject Rows() => Described(Array(Array(Value())), "One array per row, one value per column.");
		JsonObject Axis() => Object(new JsonObject { ["label"] = Text(), ["unit"] = Text() });
		JsonObject Chart() => Object(new JsonObject {
			["kind"] = Enum(AgentBlockRules.ChartKinds),
			["x"] = Object(new JsonObject { ["type"] = Enum(AgentBlockRules.XScales), ["label"] = Text(), ["unit"] = Text() }, "type"),
			["y"] = Axis(),
			["y2"] = Axis(),
			["series"] = Array(Object(new JsonObject {
				["name"] = Text(),
				["style"] = Described(Enum(AgentBlockRules.SeriesStyles),
					"comparison draws a dashed, dim line, e.g. the same metric 24 hours ago shifted onto the same x values."),
				["axis"] = Enum(AgentBlockRules.YAxes),
				["points"] = Points(),
			}, "name", "points")),
			["thresholds"] = Array(Object(new JsonObject {
				["label"] = Text(),
				["axis"] = Enum(AgentBlockRules.YAxes),
				["value"] = new JsonObject { ["type"] = "number" },
			}, "value")),
			["bands"] = Described(Array(Object(new JsonObject {
				["label"] = Text(),
				["axis"] = Enum(AgentBlockRules.BandAxes),
				["from"] = Value(),
				["to"] = Value(),
			}, "from", "to")), "Shaded ranges, e.g. a deploy window on x."),
		}, "kind", "x", "series");
		JsonObject Table() => Object(new JsonObject {
			["columns"] = Array(Object(new JsonObject {
				["name"] = Text(),
				["type"] = Described(Enum(AgentBlockRules.ColumnTypes), "level colours log levels such as error, warn, info."),
			}, "name", "type")),
			["rows"] = Rows(),
		}, "columns", "rows");
		JsonObject Tool(string name, string description, JsonObject schema) =>
			new() { ["name"] = name, ["description"] = description, ["inputSchema"] = schema };
		var show = Tool(ShowBlockTool,
			"Show the user a chart or a table in Weavie's agent pane, between your reply's text. Use it for data worth seeing as a chart, or for a large or live table; use a markdown table for a small static one. Give it an id to change it later with updateBlock. Weavie draws it in the user's theme; don't repeat its data in your reply. Limits: " + limits,
			Object(new JsonObject {
				["id"] = Described(Text(), AgentBlockRules.IdRule + "; unique in this conversation."),
				["title"] = Text(),
				["chart"] = Chart(),
				["table"] = Table(),
			}, "id", "title"));
		var update = Tool(UpdateBlockTool,
			"Change a block you showed with showBlock, in place, instead of showing or printing the data again. Pass a whole new 'chart' or 'table', or 'append' new points or rows, with append.keep holding a rolling window. Make one update per block at a time. The user watches the block change and sees when it was last updated, so don't announce an update or describe what changed unless it needs their attention. When updating blocks is all a turn did, reply with exactly " + AgentNoReply.Text + " and nothing else; Weavie shows no reply for it.",
			Object(new JsonObject {
				["id"] = Text(),
				["title"] = Text(),
				["chart"] = Chart(),
				["table"] = Table(),
				["append"] = Object(new JsonObject {
					["series"] = Array(Object(new JsonObject { ["name"] = Described(Text(), "An existing series, or a new one to add."), ["points"] = Points() }, "name", "points")),
					["rows"] = Rows(),
					["keep"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = "How many of the newest points per series, or rows, to keep." },
				}, "keep"),
			}, "id"));
		return show.ToJsonString() + "," + update.ToJsonString();
	}

	private async Task HandleBlockToolAsync(IMcpResponder responder, JsonElement args, string? idRaw, InflightCall call, bool show, CancellationToken ct) {
		string text;
		try {
			string json = args.ValueKind == JsonValueKind.Object ? args.GetRawText() : "{}";
			int bytes = Encoding.UTF8.GetByteCount(json);
			if (bytes > AgentBlockLimits.ArgumentBytes) {
				throw new AgentBlockException($"The arguments are {bytes:N0} bytes; the limit is {AgentBlockLimits.ArgumentBytes:N0}. Aggregate and resend.");
			}
			var request = JsonSerializer.Deserialize(json, AgentBlockJson.Default.AgentBlockRequest)
				?? throw new AgentBlockException("The arguments must be an object.");
			var operation = AgentBlockRules.Validate(request, call.Blocks.Find(request.Id), show);
			string kind = operation is AgentBlockOperation.Show shown ? shown.Block.Kind : "Block";
			text = call.Blocks.Stage(operation) + (show
				? $" The {kind} '{request.Id}' is shown to the user. Don't repeat its data; change it with updateBlock."
				: $" Block '{request.Id}' is updated; the user sees it change. If updating is all this turn did, reply with exactly {AgentNoReply.Text}.");
		} catch (JsonException ex) {
			await SendToolErrorAsync(responder, idRaw, $"Invalid {(show ? ShowBlockTool : UpdateBlockTool)} arguments at {ex.Path ?? "$"}: {ex.Message}", ct).ConfigureAwait(false);
			return;
		} catch (AgentBlockException ex) {
			await SendToolErrorAsync(responder, idRaw, ex.Message, ct).ConfigureAwait(false);
			return;
		}
		await SendToolTextAsync(responder, idRaw, text, ct).ConfigureAwait(false);
	}
}
