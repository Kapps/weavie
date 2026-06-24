import { expect, test } from "../harness/fixtures";

// Proves the foundation: a real Weavie.Headless boots over a throwaway git workspace, the web app reaches
// an interactive state (splash gone, layout rendered), and the claude pane launched the *fake* claude
// through the process seam (claude.path → fake wrapper).
test("app boots and launches the stubbed claude backend", async ({ weavie, page }) => {
  await expect(page.locator(".layout-root")).toBeVisible();
  await expect(page.locator(".pane-slot").first()).toBeVisible();

  // The supervisor's start line names the resolved claude binary — our fake wrapper, not the real CLI.
  await expect.poll(() => weavie.log(), { timeout: 20_000 }).toContain("fake-claude.sh");
});
