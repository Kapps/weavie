using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Hosting.Agents;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed partial class AgentSessionHostTests {
	[Fact]
	public async Task History_outline_defers_completed_bodies_without_changing_live_output() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		string body = new('x', 14 * 1024 * 1024);
		var completed = Completed("tool", body) with {
			Summary = "Read file",
			Content = [new AgentPaneContent { Type = "text", Text = "rich output" }],
			Diffs = [new AgentPaneDiff { Path = "/file", OldText = "old", NewText = "new" }],
			MediaData = "aGVsbG8=",
			ConversationId = "side-conversation",
			AnchorTurnId = "primary-turn",
		};
		fixture.Session.Emit(completed);
		await fixture.Host.DrainPaneAsync(CancellationToken.None);
		var live = Assert.Single(fixture.Bridge.PostedEventsNamed("pane"));
		Assert.False(live.GetProperty("bodyDeferred").GetBoolean());
		Assert.Equal(body, live.GetProperty("text").GetString());

		var page = Assert.Single(await HistoryPages(fixture.Host));
		Assert.True(JsonSerializer.SerializeToUtf8Bytes(AgentPaneProtocol.HistoryPage(page)).Length < 4096);
		var outline = Assert.Single(AssembleHistory([page]));
		Assert.True(outline.GetProperty("bodyDeferred").GetBoolean());
		foreach (string field in new[] { "text", "content", "diffs", "mediaData" }) {
			Assert.Equal(JsonValueKind.Null, outline.GetProperty(field).ValueKind);
		}
		Assert.Equal("Read file", outline.GetProperty("summary").GetString());
		Assert.Equal("side-conversation", outline.GetProperty("conversationId").GetString());
		Assert.Equal("primary-turn", outline.GetProperty("anchorTurnId").GetString());
		Assert.Equal("/file", Assert.Single(outline.GetProperty("locations").EnumerateArray()).GetProperty("path").GetString());
		var full = ReadBody(fixture.Host, outline);
		Assert.False(full.GetProperty("bodyDeferred").GetBoolean());
		Assert.Equal(live.GetRawText(), full.GetRawText());
	}

	[Fact]
	public async Task History_outline_keeps_prompts_controls_and_active_deltas_but_defers_user_image_data() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		foreach (string type in new[] { "user-message", "input-request", "turn-started", "agent-message-delta" }) {
			fixture.Session.Emit(Completed(type, type) with { Type = type });
		}
		fixture.Session.Emit(Completed("image", "image caption") with {
			Type = "user-image",
			MediaType = "image/png",
			MediaData = "aGVsbG8=",
		});
		fixture.Session.Emit(Completed("empty", ""));
		foreach (var outline in await History(fixture.Host)) {
			bool image = outline.GetProperty("type").GetString() == "user-image";
			Assert.Equal(image, outline.GetProperty("bodyDeferred").GetBoolean());
			if (image) {
				Assert.Equal("image caption", outline.GetProperty("text").GetString());
				Assert.Equal("image/png", outline.GetProperty("mediaType").GetString());
				Assert.Equal(JsonValueKind.Null, outline.GetProperty("mediaData").ValueKind);
				Assert.Equal("aGVsbG8=", ReadBody(fixture.Host, outline).GetProperty("mediaData").GetString());
			} else if (outline.GetProperty("itemId").GetString() != "empty") {
				Assert.Equal(outline.GetProperty("type").GetString(), outline.GetProperty("text").GetString());
			}
		}
	}

	[Fact]
	public void History_outline_preserves_blank_plan_validity_and_existing_review_locations() {
		var blankPlan = new AgentPaneRecord(1, 0, 1, Completed("plan", "   ") with { ItemType = "plan" });
		Assert.Equal(blankPlan, blankPlan.Outline());
		var edit = blankPlan with {
			Message = Completed("edit", "output") with {
				Locations = [new AgentPaneLocation { Path = "/file", Line = 42 }],
				Diffs = [new AgentPaneDiff { Path = "/file", NewText = "new" }],
			},
		};
		Assert.Equal(edit.Message.Locations, edit.Outline().Message.Locations);
	}

	[Fact]
	public async Task History_body_returns_current_revision_and_rejects_stale_generation_or_unknown_ordinal() {
		await using var fixture = CreateFixture(static () => "slot-1", 0);
		fixture.Session.Emit(Completed("first", "original"));
		fixture.Session.Emit(SideCompleted("second", "other conversation"));
		var history = await History(fixture.Host);
		var first = history[0];
		Assert.Equal("other conversation", ReadBody(fixture.Host, history[1]).GetProperty("text").GetString());
		fixture.Session.Emit(Completed("first", "updated"));
		var updated = ReadBody(fixture.Host, first);
		Assert.Equal("updated", updated.GetProperty("text").GetString());
		Assert.True(updated.GetProperty("revision").GetInt64() > first.GetProperty("revision").GetInt64());
		Assert.Throws<InvalidOperationException>(() => fixture.Host.ReadHistoryBody(new(
			first.GetProperty("generation").GetInt64(), long.MaxValue)));
		fixture.Session.Emit(new AgentPaneMessage { Type = "transcript-reset", ProviderId = "structured" });
		fixture.Session.Emit(Completed("replacement", "new generation"));
		Assert.Throws<InvalidOperationException>(() => ReadBody(fixture.Host, first));
	}

	private static JsonElement ReadBody(AgentSessionHost host, JsonElement outline) =>
		JsonSerializer.SerializeToElement(AgentPaneProtocol.Message(host.ReadHistoryBody(new(
			outline.GetProperty("generation").GetInt64(), outline.GetProperty("ordinal").GetInt64()))));
}
