import { expect, test } from "../harness/fixtures";

// Claude (the fake) proposes an edit via the IDE MCP openDiff tool → Weavie renders an inline review with
// Keep/Revert → the user keeps it → the editor adopts the proposed content. This is the change-review seam
// end to end. The hook gate + diff presentation are loopback inside the worker in both transports, so the
// review UI is the same; tagged @cross to also exercise it over the remote bridge.
test.use({
  fakeScript: {
    steps: [
      // Let the page connect before the diff is pushed (openDiff blocks until the user resolves it).
      { op: "sleep", ms: 1500 },
      {
        op: "mcp",
        server: "ide",
        tool: "openDiff",
        args: {
          old_file_path: "{{WORKSPACE}}/hello.ts",
          new_file_path: "{{WORKSPACE}}/hello.ts",
          new_file_contents: "// DIFF_MARKER kept\nexport const answer = 42;\n",
          tab_name: "hello.ts",
        },
      },
    ],
  },
});

test("claude opens a diff and keeping it applies the change @cross", async ({ page }) => {
  const keep = page.locator(".weavie-inline-accept");
  await expect(keep).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  await keep.click();

  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
  await expect(page.locator(".monaco-editor .view-lines")).toContainText("DIFF_MARKER");
});

// Require-approval: a tool call that needs permission fires the PermissionRequest hook → Weavie surfaces an
// approval prompt → the user accepts/denies. The fake can send the hook (op:"hook"); the remaining piece is
// driving the approval affordance in the UI. Left for a follow-up.
test.fixme("a tool call requiring approval prompts and proceeds on accept @cross", async () => {});

// Cross-session diff navigation: open diffs in two sessions and walk between them. Needs two concurrent
// sessions each with a pending diff plus the between-session nav affordance. Left for a follow-up.
test.fixme("diff navigation moves between sessions @cross", async () => {});
