using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	private async Task<JsonNode> SteerVisibilityAsync(bool sharedMessageId, string replyKind, CancellationToken ct) {
		string? messageId = sharedMessageId ? "shared-response" : null;
		var steered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) (_heldPrompt, _heldMode) = (steered, "steer-visibility");
		VisibilityTool("build", "tool_call", "in_progress");
		VisibilityContent("agent_thought_chunk", "Inspecting the build before your question.", messageId);
		using var registration = ct.Register(() => steered.TrySetCanceled(ct));
		await steered.Task.ConfigureAwait(false);
		lock (_gate) _heldPrompt = null;
		// The test releases this after observing the accepted steer, so transport timing cannot reorder the fixture.
		await HoldOnceAsync("steer-reply", ct).ConfigureAwait(false);
		VisibilityContent(replyKind, "I'm running the build directly ", messageId);
		VisibilityTool("build", "tool_call_update", "completed");
		VisibilityContent(replyKind, "in this session.", messageId);
		VisibilityTool("verify", "tool_call", "in_progress");
		VisibilityContent("agent_thought_chunk", "Checking the build result.", messageId);
		await HoldOnceAsync("steer-finish", ct).ConfigureAwait(false);
		VisibilityTool("verify", "tool_call_update", "completed");
		VisibilityContent("agent_message_chunk", "Build verified.", messageId);
		return new JsonObject { ["stopReason"] = "end_turn" };
	}

	private void VisibilityContent(string kind, string text, string? messageId) {
		Update(new JsonObject {
			["sessionUpdate"] = kind,
			["messageId"] = messageId,
			["content"] = Text(text),
		});
	}

	private void VisibilityTool(string id, string kind, string status) {
		Update(new JsonObject {
			["sessionUpdate"] = kind,
			["toolCallId"] = id,
			["title"] = id == "build" ? "Build application" : "Verify application",
			["kind"] = "execute",
			["status"] = status,
		});
	}
}
