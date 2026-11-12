using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Core.Mcp;

// showBlock / updateBlock: a chart or table drawn in the native agent pane. See docs/specs/acp-rich-content/design.md.
public sealed partial class McpServer {
	private const string ShowBlockTool = "showBlock";
	private const string UpdateBlockTool = "updateBlock";

	private const string BlockValue = """{"type":["number","string","null"]}""";
	private const string BlockPoints =
		"""{"type":"array","description":"[x, y] pairs. A time x is Unix milliseconds or ISO 8601.","items":{"type":"array","items":"""
		+ BlockValue + ""","minItems":2,"maxItems":2}}""";
	private const string BlockRows = """{"type":"array","description":"One array per row, one value per column.","items":{"type":"array","items":"""
		+ BlockValue + "}}";
	private const string BlockAxis = """{"type":"object","properties":{"label":{"type":"string"},"unit":{"type":"string"}}}""";
	private const string BlockChart =
		"""{"type":"object","properties":{"kind":{"type":"string","enum":["line","area","bar","scatter"]},"x":{"type":"object","properties":{"type":{"type":"string","enum":["time","category","number"]},"label":{"type":"string"},"unit":{"type":"string"}},"required":["type"]},"y":"""
		+ BlockAxis + ""","y2":""" + BlockAxis
		+ ""","series":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"style":{"type":"string","enum":["solid","comparison"],"description":"comparison draws a dashed, dim line, e.g. the same metric 24 hours ago shifted onto the same x values."},"axis":{"type":"string","enum":["y","y2"]},"points":"""
		+ BlockPoints
		+ """},"required":["name","points"]}},"thresholds":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"axis":{"type":"string","enum":["y","y2"]},"value":{"type":"number"}},"required":["value"]}},"bands":{"type":"array","description":"Shaded ranges, e.g. a deploy window on x.","items":{"type":"object","properties":{"label":{"type":"string"},"axis":{"type":"string","enum":["x","y","y2"]},"from":"""
		+ BlockValue + ""","to":""" + BlockValue + """},"required":["from","to"]}}},"required":["kind","x","series"]}""";
	private const string BlockTable =
		"""{"type":"object","properties":{"columns":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"type":{"type":"string","enum":["time","number","text","level"],"description":"level colours log levels such as error, warn, info."}},"required":["name","type"]}},"rows":"""
		+ BlockRows + """},"required":["columns","rows"]}""";

	private const string BlockToolEntries =
		"""{"name":"showBlock","description":"Show the user a chart or a table in Weavie's agent pane, between your reply's text. Use it for data worth seeing as a chart, or for a large or live table; use a markdown table for a small static one. Give it an id to change it later with updateBlock. Weavie draws it in the user's theme; don't repeat its data in your reply. Limits: 12 series of 5,000 points, 2,000 rows of 24 columns.","inputSchema":{"type":"object","properties":{"id":{"type":"string","description":"1-64 letters, digits, '.', '_' or '-'; unique in this conversation."},"title":{"type":"string"},"chart":"""
		+ BlockChart + ""","table":""" + BlockTable + """},"required":["id","title"]}},"""
		+ """{"name":"updateBlock","description":"Change a block you showed with showBlock, in place, instead of showing or printing the data again. Pass a whole new 'chart' or 'table', or 'append' new points or rows with 'keep' to hold a rolling window. Make one update per block at a time. The block shows when it was last updated.","inputSchema":{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"chart":"""
		+ BlockChart + ""","table":""" + BlockTable
		+ ""","append":{"type":"object","properties":{"series":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string","description":"An existing series."},"points":"""
		+ BlockPoints + """},"required":["name","points"]}},"rows":""" + BlockRows
		+ """}},"keep":{"type":"integer","description":"With append: how many of the newest points per series, or rows, to keep."}},"required":["id"]}}""";

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
				: $" Block '{request.Id}' is updated.");
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
