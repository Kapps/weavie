using System.Text.Json;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexPaneMessagesTests {
	[Fact]
	public void FromRequest_WithoutParams_ProducesPendingInputMessage() {
		using var doc = JsonDocument.Parse("""{"id":4,"method":"item/tool/requestUserInput"}""");
		var message = CodexPaneMessages.FromRequest(new CodexServerRequest("4", 4L, "item/tool/requestUserInput", doc.RootElement.Clone()));

		Assert.Equal("input-requested", message.Type);
		Assert.Equal("4", message.ItemId);
		Assert.Equal("item/tool/requestUserInput", message.ItemType);
		Assert.Equal("pending", message.Status);
	}

	[Fact]
	public void FromRequest_UserInput_SummarizesQuestion() {
		using var doc = JsonDocument.Parse(
			"""{"id":4,"method":"item/tool/requestUserInput","params":{"threadId":"thread_1","turnId":"turn_1","questions":[{"header":"Mode","id":"mode","question":"Which mode?","options":[{"label":"Safe","description":"Use safe mode."}]}]}}""");

		var message = CodexPaneMessages.FromRequest(new CodexServerRequest("input-4", "input-4", "item/tool/requestUserInput", doc.RootElement.Clone()));

		Assert.Equal("input-requested", message.Type);
		Assert.Equal("input-4", message.ItemId);
		Assert.Equal("thread_1", message.ThreadId);
		Assert.Equal("turn_1", message.TurnId);
		Assert.Equal("Which mode?", message.Summary);
	}

	[Fact]
	public void InputResponse_BuildsAppServerAnswerShape() {
		object response = CodexInputResponses.Build(new Dictionary<string, IReadOnlyList<string>> {
			["mode"] = ["Safe"],
		});

		using var doc = JsonDocument.Parse(JsonSerializer.Serialize(response));

		Assert.Equal("Safe", doc.RootElement.GetProperty("answers").GetProperty("mode").GetProperty("answers")[0].GetString());
	}

	[Fact]
	public void FromNotification_MapsFilePatchUpdated() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/fileChange/patchUpdated","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_1","changes":[{"path":"src/App.cs","diff":"@@","kind":{"type":"update"}}]}}""");

		var message = CodexPaneMessages.FromNotification(
			"item/fileChange/patchUpdated",
			"thread_1",
			doc.RootElement);

		Assert.Equal("file-patch-updated", message.Type);
		Assert.Equal("thread_1", message.ThreadId);
		Assert.Equal("turn_1", message.TurnId);
		Assert.Equal("item_1", message.ItemId);
		Assert.Equal("src/App.cs", message.Summary);
	}

	[Fact]
	public void FromNotification_MapsPlanDelta() {
		using var doc = JsonDocument.Parse(
			"""{"method":"item/plan/delta","params":{"threadId":"thread_1","turnId":"turn_1","itemId":"item_1","delta":"- inspect"}}""");

		var message = CodexPaneMessages.FromNotification("item/plan/delta", "thread_1", doc.RootElement);

		Assert.Equal("delta", message.Type);
		Assert.Equal("item_1", message.ItemId);
		Assert.Equal("- inspect", message.Text);
	}
}
