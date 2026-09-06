import { readFileSync } from "node:fs";
import { join } from "node:path";
import { activeSessionSlot, createSession, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The change-review seam, driven by the fake claude's IDE-MCP openDiff. The hook gate + diff presentation
// are loopback inside the worker on both transports, so the review UI is identical; tagged @cross to also
// exercise it over the remote bridge.

const sleep = { op: "sleep" as const, ms: 1500 };
function openDiff(contents: string) {
  return {
    op: "mcp" as const,
    server: "ide" as const,
    tool: "openDiff",
    args: {
      old_file_path: "{{WORKSPACE}}/hello.ts",
      new_file_path: "{{WORKSPACE}}/hello.ts",
      new_file_contents: contents,
      tab_name: "hello.ts",
    },
  };
}

test.describe("openDiff review", () => {
  test.use({
    fakeScript: { steps: [sleep, openDiff("// DIFF_MARKER kept\nexport const answer = 42;\n")] },
  });

  test("keeping a proposed edit applies the change @cross", async ({ page, weavie }) => {
    const keep = page.locator(".weavie-inline-accept");
    await expect(keep).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

    await keep.click();

    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("DIFF_MARKER");
    await expect
      .poll(() => readFileSync(join(weavie.workspace, "hello.ts"), "utf8"))
      .toContain("DIFF_MARKER");
  });
});

test.describe("large openDiff review", () => {
  const contents = Array.from({ length: 5_000 }, (_, index) => `// LARGE DIFF ${index}`).join("\n");
  test.use({ fakeScript: { steps: [sleep, openDiff(contents)] } });

  test("renders the diff and the Keep shortcut resolves it @cross", async ({ page }) => {
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".weavie-inline-toolbar")).not.toContainText("timed out");
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("LARGE DIFF");
  });

  test("renders the diff and the Reject shortcut resolves it", async ({ page }) => {
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".weavie-inline-toolbar")).not.toContainText("timed out");
    await page.keyboard.press("ControlOrMeta+Backspace");
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
  });
});

test.describe("change navigation", () => {
  // Two separated edits → two hunks, so the review walk has something to navigate.
  const twoHunks =
    "export function greet(name: string): string {\n" +
    "  return `Hi there, ${name}!`;\n" +
    "}\n\n" +
    'const message = greet("weavie");\n' +
    "console.error(message);\n";
  test.use({ fakeScript: { steps: [sleep, openDiff(twoHunks)] } });

  // 2026-09-06 01:24 UTC: flaked on the macOS shard — https://github.com/Kapps/weavie/actions/runs/34003547684/job/101406941253.
  // Root cause: the assertion sampled the cursor DOM node's pixel `top`, comparing it against itself after
  // navigating away and back — a fragile proxy that can read the same value on a legitimate wrap-around and
  // races the async caret reveal on a slow runner. Fixed in Kapps/weavie#785 by reading the editor's actual
  // line number instead and asserting the exact destinations (2 → 6 → 2), which is deterministic and needs
  // no polling tolerance.
  test("the next-change control moves through the diff's hunks @cross", async ({ page }) => {
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });
    const caretLine = () =>
      page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()?.lineNumber);
    await expect.poll(caretLine).toBe(2);

    const next = page.locator(".weavie-inline-nav").nth(1); // ↓ next change
    await next.click();
    await expect.poll(caretLine).toBe(6);

    await next.click();
    await expect.poll(caretLine).toBe(2);
  });
});

test.describe("per-session diff state", () => {
  test.use({ fakeScript: { steps: [sleep, openDiff("// SESSION_A_DIFF\n")] } });

  // "Diff navigation between sessions": diffs are per-session state, so switching away and back to a session
  // restores its review — not a global walk across sessions.
  test("a session keeps its diff across a switch @cross", async ({ page }) => {
    const chips = page.locator(".session-chip");
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });

    const primarySlot = await activeSessionSlot(page);
    await createSession(page, { branch: "e2e/diff-session", provider: "claude" });
    await expect(chips).toHaveCount(2);
    await waitForSessionSwitch(page, primarySlot);

    // Back to the first session — its diff review is still there.
    await chips.first().click();
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("SESSION_A_DIFF");
  });
});
