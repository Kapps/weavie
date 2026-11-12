using Weavie.Core.Agents;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentBlockTests {
	private static readonly AgentChart Line = new() {
		Kind = "line",
		X = new() { Type = "time" },
		Series = [new() { Name = "now", Points = [[new(null, "2026-11-12T14:00:00Z"), new(null, "318")]] }],
	};

	[Fact]
	public void ShowNormalizesTimesAndNumbers() {
		var show = Assert.IsType<AgentBlockOperation.Show>(AgentBlockRules.Validate(new() { Id = "p95", Title = " p95 ", Chart = Line }, null, show: true));

		Assert.Equal("p95", show.Block.Title);
		Assert.Equal(DateTimeOffset.Parse("2026-11-12T14:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), show.Block.Chart!.Series[0].Points[0][0].Number);
		Assert.Equal(318, show.Block.Chart.Series[0].Points[0][1].Number);
		Assert.Null(show.Block.UpdatedAtMs);
	}

	[Theory]
	[InlineData("""{"id":"p95","title":"t","chart":{"kind":"line","x":{"type":"number"},"series":[{"name":"s","points":[[1,"x"]]}]}}""", "chart.series[0].points[0][1] is 'x'; it must be a number.")]
	[InlineData("""{"id":"p95","title":"t","chart":{"kind":"line","x":{"type":"number"},"series":[{"name":"s","axis":"y2","points":[]}]}}""", "chart.series[0] uses axis y2, but the chart has no y2.")]
	[InlineData("""{"id":"p 95","title":"t","table":{"columns":[{"name":"a","type":"text"}],"rows":[]}}""", "id must be 1-64 letters, digits, '.', '_' or '-'.")]
	[InlineData("""{"id":"p95","title":"t","table":{"columns":[{"name":"a","type":"text"}],"rows":[["a","b"]]}}""", "table.rows[0] has 2 values; the table has 1 columns.")]
	[InlineData("""{"id":"p95","title":"t","chart":{"kind":"line","x":{"type":"time"},"series":[{"name":"s","points":[null]}]}}""", "chart.series[0].points[0] must not be null.")]
	[InlineData("""{"id":"p","title":"t","chart":{"kind":"line","x":{"type":"number"},"series":[{"name":"s","points":[]}],"thresholds":[{"value":1e999}]}}""", "chart.thresholds[0].value must be a finite number.")]
	public void RefusalsNameTheField(string json, string message) {
		var request = System.Text.Json.JsonSerializer.Deserialize(json, AgentBlockJson.Default.AgentBlockRequest)!;
		Assert.Equal(message, Assert.Throws<AgentBlockException>(() => AgentBlockRules.Validate(request, null, show: true)).Message);
	}

	[Theory]
	[InlineData("""{"id":null,"title":"t"}""")]
	[InlineData("""{"id":"p","title":"t","chart":{"kind":"line","x":null,"series":[]}}""")]
	[InlineData("""{"id":"p","title":"t","chart":{"kind":"line","x":{"type":"number"},"series":[{"name":"s","points":[[1e999,1]]}]}}""")]
	public void NullsAndNonFiniteNumbersFailAsJson(string json) =>
		Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize(json, AgentBlockJson.Default.AgentBlockRequest));

	[Fact]
	public void TooManyPointsIsRefusedWithTheLimit() {
		var points = Enumerable.Range(0, AgentBlockLimits.Points + 1).Select(index => (IReadOnlyList<AgentBlockValue>)[new(index, null), new(index, null)]).ToList();
		var chart = Line with { X = new() { Type = "number" }, Series = [new() { Name = "s", Points = points }] };

		var error = Assert.Throws<AgentBlockException>(() => AgentBlockRules.Validate(new() { Id = "p", Title = "t", Chart = chart }, null, show: true));
		Assert.Equal("chart.series[0].points has 5,001 points; the limit is 5,000 per series. Aggregate and resend.", error.Message);
	}

	[Fact]
	public void UpdatesApplyToTheCurrentBlockOnly() {
		var shown = ((AgentBlockOperation.Show)AgentBlockRules.Validate(new() { Id = "p95", Title = "t", Chart = Line }, null, show: true)).Block;
		Assert.Throws<AgentBlockException>(() => AgentBlockRules.Validate(new() { Id = "p95", Title = "t", Chart = Line }, shown, show: true));
		Assert.Throws<AgentBlockException>(() => AgentBlockRules.Validate(new() { Id = "p95", Chart = Line }, null, show: false));
		var added = AgentBlockRules.Apply(shown, AgentBlockRules.Validate(new() {
			Id = "p95",
			Keep = 2,
			Append = new() { Series = [new() { Name = "other", Points = [[new(9, null), new(9, null)]] }] },
		}, shown, show: false), 1)!;
		Assert.Equal(["now", "other"], added.Chart!.Series.Select(series => series.Name));

		var append = AgentBlockRules.Validate(new() {
			Id = "p95",
			Keep = 2,
			Append = new() { Series = [new() { Name = "now", Points = [[new(2, null), new(2, null)], [new(3, null), new(3, null)]] }] },
		}, shown, show: false);
		var updated = AgentBlockRules.Apply(shown, append, 42)!;

		Assert.Equal([2.0, 3.0], updated.Chart!.Series[0].Points.Select(point => point[1].Number!.Value));
		Assert.Equal(42, updated.UpdatedAtMs);
		Assert.Null(AgentBlockRules.Apply(null, append, 42));
		Assert.Null(AgentBlockRules.Apply(updated, new AgentBlockOperation.Show(shown), 42));
	}

	[Fact]
	public void AnAppendValidatedAgainstAnOlderTableNoLongerApplies() {
		var columns = new AgentTable { Columns = [new() { Name = "a", Type = "text" }], Rows = [] };
		var shown = new AgentBlock { Id = "t", Title = "t", Table = columns };
		var append = AgentBlockRules.Validate(new() { Id = "t", Keep = 5, Append = new() { Rows = [[new(null, "x")]] } }, shown, show: false);
		var widened = shown with { Table = columns with { Columns = [.. columns.Columns, new() { Name = "b", Type = "text" }] } };

		Assert.Null(AgentBlockRules.Apply(widened, append, 1));
		Assert.Single(AgentBlockRules.Apply(shown, append, 1)!.Table!.Rows);
	}

	[Fact]
	public void ClaimsAreSingleUseAndFoundInAnyRelayShape() {
		var claims = new AgentBlockClaims();
		var operation = new AgentBlockOperation.Show(new() { Id = "a", Title = "a" });
		string token = claims.Stage(operation);

		Assert.Null(claims.Take("weavie-block:" + new string('0', 32), null));
		Assert.Same(operation, claims.Take("{\"result\":{\"content\":[{\"text\":\"" + token + " shown\"}]}}", null));
		Assert.Null(claims.Take(token, null));
		string fenced = claims.Stage(operation);
		Assert.Same(operation, claims.Take(null, [new AgentPaneContent { Type = "text", Text = "```\n" + fenced + "\n```" }]));
		claims.Stage(operation);
		Assert.Single(claims.Drain());
		Assert.Empty(claims.Drain());
		Assert.Throws<ToolUnavailableException>(() => claims.Find("a"));
		claims.Bind(_ => null);
		Assert.Throws<InvalidOperationException>(() => claims.Bind(_ => null));
	}

	[Fact]
	public void OutputDropsABlockWithItsRow_AndSendsOnlyNewerBlocksInHistory() {
		var output = new ConversationOutput(new NullSink());
		var row = new AgentPaneMessage { Type = "item-completed", ProviderId = "p", TurnId = "1", ItemId = "tool:1", ItemType = "block", BlockId = "a" };
		var state = new AgentBlockState("p", null, new() { Id = "a", Title = "a" });
		output.Restore([row], [state]);
		Assert.NotNull(output.Block("p", null, "a"));
		var baseline = output.ReadHistory(null, null);
		Assert.Single(baseline.Blocks);

		output.WriteBlock(state, row with { Type = "item-completed" }, () => { });
		Assert.Single(output.ReadHistory(baseline.Generation, baseline.Revision).Blocks);
		output.Write(new() { Type = "item-retracted", ProviderId = "p", TurnId = "1", ItemId = "tool:1" }, () => { });
		Assert.Null(output.Block("p", null, "a"));
	}

	private sealed class NullSink : IConversationOutputSink {
		public void Live(AgentPaneRecord record) { }
		public void Reset() { }
		public void Block(AgentBlockRecord record) { }
	}
}
