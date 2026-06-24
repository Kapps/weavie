import { expect, test } from "../harness/fixtures";

// The permission gate ("require approval"). There is no web prompt — approval is the hook bridge's decision
// (HookPolicy), the real seam Weavie owns. The fake sends a PermissionRequest over the named pipe and we
// assert the decision it gets back, end to end (fake → pipe → HookBridgeServer → HookPolicy → decision).
// @cross: the gate is loopback inside the worker on both transports.

const bashPermission = {
  op: "hook" as const,
  request: {
    hook_event_name: "PermissionRequest",
    tool_name: "Bash",
    tool_input: { command: "echo hi" },
  },
};

// The fake logs `hook -> <decision json>`; isolate that line so the setSetting echo (which contains the
// "allowAllTools" key) can't be mistaken for an allow decision.
function hookDecision(log: string): string {
  return log.split("\n").find((line) => line.startsWith("hook ->")) ?? "";
}

test.describe("permission gate — default (claude.allowAllTools off)", () => {
  test.use({ fakeScript: { steps: [bashPermission] } });

  test("a tool call passes through to claude's own prompt @cross", async ({ weavie }) => {
    // PassThrough → empty decision: Weavie defers to claude's own permission flow, never auto-allows.
    await expect
      .poll(() => hookDecision(weavie.fakeLog()), { timeout: 20_000 })
      .toMatch(/^hook ->\s*$/);
  });
});

test.describe("permission gate — claude.allowAllTools on", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "claude.allowAllTools", value: true } },
        bashPermission,
      ],
    },
  });

  test("a non-edit tool is auto-allowed @cross", async ({ weavie }) => {
    await expect.poll(() => hookDecision(weavie.fakeLog()), { timeout: 20_000 }).toContain("allow");
  });
});
