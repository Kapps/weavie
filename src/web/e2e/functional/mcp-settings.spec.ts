import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Claude (the fake) edits a Weavie setting over the registry MCP server, and the change reflects in the UI.
// editor.minimap defaults off, so turning it on must make Monaco render the minimap. This exercises the
// whole capability-registry round-trip: fake claude → ws+bearer → McpServer → SettingsStore → editorOptions
// push → web applies it. The MCP server is loopback inside the worker in both transports, so headless-only.
test.use({
  fakeScript: {
    steps: [{ op: "mcp", tool: "setSetting", args: { key: "editor.minimap", value: true } }],
  },
});

test("claude edits a setting over MCP and the editor reflects it", async ({ page }) => {
  await openFile(page, "hello.ts");
  await expect(page.locator(".monaco-editor .minimap")).toBeVisible({ timeout: 15_000 });
});
