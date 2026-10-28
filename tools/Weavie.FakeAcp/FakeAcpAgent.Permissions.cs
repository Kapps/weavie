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

	// Mirrors claude-agent-acp 0.99 with AIR planFile: the plan is a file the agent writes outside the workspace, the
	// approval names it, and approval reads it back, releases it, and publishes it as plan-mode:<id>.
	private async Task ExitPlanModeAsync(CancellationToken ct) {
		string id = $"toolu_plan_{++_permissionSequence}";
		string write = $"toolu_write_{_permissionSequence}";
		string path = Path.Combine(_stateDirectory, "plans", "fake-plan.md");
		const string plan = "# Claude plan\n\nCreate b.txt.\n";
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, plan);
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = write,
			["title"] = "Write " + path,
			["kind"] = "edit",
			["status"] = "pending",
			["locations"] = new JsonArray(new JsonObject { ["path"] = path })
		});
		PermissionToolUpdate(write, "completed");
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = id,
			["title"] = "Approve Plan",
			["kind"] = "switch_mode",
			["status"] = "pending",
			["content"] = new JsonArray()
		});
		Update(new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = id, ["rawInput"] = PlanFile(path) });
		var result = await Connection().RequestAsync("session/request_permission", new JsonObject {
			["sessionId"] = _sessionId,
			["toolCall"] = new JsonObject { ["toolCallId"] = id, ["title"] = "Approve Plan", ["rawInput"] = PlanFile(path) },
			["options"] = new JsonArray(
				new JsonObject { ["optionId"] = "exit-plan-default", ["name"] = "Yes, manually approve edits", ["kind"] = "allow_once" },
				new JsonObject { ["optionId"] = "exit-plan-bypass", ["name"] = "Yes, and bypass permissions", ["kind"] = "allow_always" },
				new JsonObject { ["optionId"] = "reject", ["name"] = "No, keep planning", ["kind"] = "reject_once" }),
		}, ct).ConfigureAwait(false);
		var outcome = result.GetProperty("outcome");
		bool approved = outcome.GetProperty("outcome").GetString() == "selected"
			&& outcome.GetProperty("optionId").GetString() != "reject";
		if (!approved) {
			PermissionToolUpdate(id, "failed");
			Message("kept planning");
			return;
		}
		string approvedPlan = File.ReadAllText(path);
		_mode = "default";
		Update(new JsonObject { ["sessionUpdate"] = "current_mode_update", ["currentModeId"] = _mode });
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call_update",
			["toolCallId"] = id,
			["status"] = "completed",
			["rawInput"] = PlanFile(path),
			["title"] = "Exited Plan Mode"
		});
		Update(new JsonObject {
			["sessionUpdate"] = "tool_call",
			["toolCallId"] = id + ":plan-file-clear",
			["title"] = "Exited Plan Mode",
			["kind"] = "switch_mode",
			["status"] = "completed",
			["rawInput"] = PlanFile(""),
			["content"] = new JsonArray()
		});
		PlanDocument("plan-mode:" + id, approvedPlan);
		Message("approved plan: " + approvedPlan);
	}

	// A mode switch whose content is a notice, not a plan to approve.
	private void ModeSwitch() => Update(new JsonObject {
		["sessionUpdate"] = "tool_call",
		["toolCallId"] = "mode-switch",
		["title"] = "Switch mode",
		["kind"] = "switch_mode",
		["status"] = "completed",
		["content"] = new JsonArray(new JsonObject { ["type"] = "content", ["content"] = Text("Switched to code mode.") }),
	});

	private static JsonObject PlanFile(string path) => new() { ["planFilePath"] = path };

	private void PermissionToolUpdate(string id, string status) => Update(new JsonObject {
		["sessionUpdate"] = "tool_call_update",
		["toolCallId"] = id,
		["status"] = status,
	});
}
