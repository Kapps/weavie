using System.Text.Json.Nodes;

namespace Weavie.FakeAcp;

internal sealed partial class FakeAcpAgent {
	private int _permissionSequence;
	private static readonly string FullPlanMarkdown = "# Detailed work plan\n\n## Summary\n\nKeep plan review explicit and display the full document.\n\n"
		+ string.Join("\n\n", Enumerable.Range(1, 12).Select(index =>
			$"## Implementation step {index}\n\nPreserve session ownership, serialize updates, and retain the complete Markdown document.\n\n- Update the authoritative state.\n- Verify the user-visible result."))
		+ "\n\n## Validation\n\n```text\nrequest → review → implement\n```\n\nEnd of the complete work plan.";

	private async Task PermissionLifecycleAsync(string scenario, CancellationToken ct) {
		bool plan = scenario.StartsWith("plan", StringComparison.Ordinal);
		bool existing = scenario is "existing" or "plan-existing";
		bool edit = scenario is "edit" or "edit-only" or "edit-hold";
		string id = $"{(plan ? "plan-review" : "permission")}:{++_permissionSequence}";
		string path = Path.Combine(Environment.CurrentDirectory, "permission-edit.txt");
		var tool = new JsonObject {
			["toolCallId"] = id,
			["title"] = plan ? "Implement this plan?" : "Protected operation",
			["kind"] = plan ? "switch_mode" : edit ? "edit" : "execute",
			["status"] = "pending",
			["rawInput"] = plan ? new JsonObject { ["plan"] = FullPlanMarkdown } : JsonValue.Create("protected input"),
		};
		if (edit) tool["locations"] = new JsonArray(new JsonObject { ["path"] = path });
		if (plan) PlanDocument("live-plan", FullPlanMarkdown);
		if (existing) {
			var initial = (JsonObject)tool.DeepClone();
			initial["sessionUpdate"] = "tool_call";
			Update(initial);
		}
		if (scenario == "unknown") {
			PermissionToolUpdate(id, "completed");
			return;
		}
		var request = Connection().RequestAsync("session/request_permission", new JsonObject {
			["sessionId"] = _sessionId,
			["toolCall"] = existing || scenario == "id-only" ? new JsonObject { ["toolCallId"] = id } : tool,
			["options"] = new JsonArray(
				new JsonObject { ["optionId"] = "allow", ["name"] = plan ? "Yes, implement this plan" : "Allow", ["kind"] = "allow_once" },
				new JsonObject { ["optionId"] = "reject", ["name"] = plan ? "No, revise this plan" : "Reject", ["kind"] = "reject_once" }),
		}, ct);
		if (scenario == "immediate") PermissionToolUpdate(id, "in_progress");
		var result = await request.ConfigureAwait(false);
		string outcome = result.GetProperty("outcome").GetProperty("outcome").GetString()!;
		string decision = outcome == "selected" ? result.GetProperty("outcome").GetProperty("optionId").GetString()! : outcome;
		if (scenario is "initial" or "duplicate") {
			var initial = (JsonObject)tool.DeepClone();
			initial["sessionUpdate"] = "tool_call";
			Update(initial);
			if (scenario == "duplicate") Update((JsonObject)initial.DeepClone());
		}
		if (edit && decision == "allow") File.WriteAllText(path, "after permission\n");
		if (scenario == "edit-hold") {
			Message("permission approved and held");
			await _never.Task.WaitAsync(ct).ConfigureAwait(false);
		}
		if (scenario != "edit-only") PermissionToolUpdate(id, decision == "allow" ? "completed" : "failed");
		Message("permission lifecycle: " + decision);
	}

	private void PermissionToolUpdate(string id, string status) => Update(new JsonObject {
		["sessionUpdate"] = "tool_call_update",
		["toolCallId"] = id,
		["status"] = status,
	});
}
