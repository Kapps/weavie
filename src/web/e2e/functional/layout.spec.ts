import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Toggling the fullscreen-pane command collapses the layout to a single visible pane and restores it.
// Non-fullscreen panes are hidden (display:none), so the count of visible pane slots is the observable.
// Pure frontend layout, so headless-only.
test("fullscreen pane toggle hides the other panes and restores them", async ({ page }) => {
  const visibleSlots = () => page.locator(".pane-slot:visible").count();
  const initial = await visibleSlots();
  expect(initial).toBeGreaterThan(1);

  await runCommand(page, "Toggle Fullscreen Pane");
  await expect.poll(visibleSlots, { timeout: 10_000 }).toBe(1);

  await runCommand(page, "Toggle Fullscreen Pane");
  await expect.poll(visibleSlots, { timeout: 10_000 }).toBe(initial);
});
