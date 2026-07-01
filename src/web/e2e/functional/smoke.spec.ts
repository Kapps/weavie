import { expect, test } from "../harness/fixtures";

// Proves the foundation: a real Weavie.Headless boots over a throwaway git workspace, the web app reaches
// an interactive state (splash gone, layout rendered), and the claude pane launched the *fake* claude
// through the process seam (claude.path → fake wrapper).
test("app boots and launches the stubbed claude backend", async ({ weavie, page }) => {
  await expect(page.locator(".layout-root")).toBeVisible();
  await expect(page.locator(".pane-slot").first()).toBeVisible();

  // Editor init succeeded: "editor host ready" logs only when createEditorHost completes (a failed init logs
  // "editor init failed"). Guards the init-order race the splash-gone gate misses; no file open ⇒ no DOM node to assert.
  await expect.poll(() => weavie.log(), { timeout: 40_000 }).toContain("editor host ready");

  // The supervisor's start line names the resolved claude binary — our fake wrapper (.sh on POSIX, .cmd on
  // Windows), not the real CLI.
  await expect.poll(() => weavie.log(), { timeout: 20_000 }).toContain("fake-claude.");
});
