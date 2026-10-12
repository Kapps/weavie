import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The session switcher: a rail of status dots, the session panel (hover, Ctrl+E, pin), and live agent
// activity from the fake ACP agent.
test.use({
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: false } },
    ],
  },
});

test("hover and Ctrl+E open the panel; arrows switch; pinning docks it across a reload", async ({
  page,
}) => {
  await createAcpSession(page, "feature/panel-second");
  const panel = page.locator(".session-panel");
  const rows = panel.locator(".session-panel-row");

  await page.locator(".session-rail").hover();
  await expect(rows).toHaveCount(2);
  await expect(rows.filter({ hasText: "panel second" })).toHaveClass(/\bactive\b/);
  await page.locator(".pane-area").hover();
  await expect(panel).toBeHidden();

  // Opened from the keyboard, it closes when focus moves elsewhere.
  await page.keyboard.press("ControlOrMeta+e");
  await expect(panel).toBeVisible();
  await page.locator(".tb-omnibar-input").click();
  await page.keyboard.press("Escape");
  await expect(panel).toBeHidden();

  const before = await activeSessionSlot(page);
  await page.locator(".xterm:visible").first().click();
  await page.keyboard.press("ControlOrMeta+e");
  await expect(rows.filter({ hasText: "panel second" })).toBeFocused();
  const other = rows.filter({ hasNotText: "panel second" });
  // Both sessions are idle, so they share a group in rail order: the workspace session first.
  await page.keyboard.press("ArrowUp");
  await expect(other).toBeFocused();
  await page.keyboard.press("Enter");
  await waitForSessionSwitch(page, before);
  await expect(panel).toBeHidden();
  // Focus is carried into the new session rather than dropped on the page.
  await expect.poll(() => page.evaluate(() => document.activeElement !== document.body)).toBe(true);

  // A modal makes the rail inert, which swallows its pointer leave; hover still works afterwards.
  await page.locator(".session-rail-add").click();
  await page.keyboard.press("Escape");
  await page.locator(".pane-area").hover();
  await page.locator(".session-rail").hover();
  await expect(panel).toBeVisible();
  await page.locator(".pane-area").hover();
  await expect(panel).toBeHidden();

  const left = async (): Promise<number> =>
    (await page.locator(".pane-area").boundingBox())?.x ?? Number.NaN;
  const unpinned = await left();
  await page.locator(".session-rail").hover();
  await panel.getByRole("button", { name: /Pin session panel/ }).click();
  await expect(panel).toHaveClass(/\bpinned\b/);
  expect(await left()).toBeGreaterThan(unpinned + 200);

  await page.reload();
  await expect(page.locator(".session-panel.pinned")).toBeVisible();
  // Pinned, Escape hands focus back instead of closing anything.
  await page.locator(".xterm:visible").first().click();
  await page.keyboard.press("ControlOrMeta+e");
  await expect(panel.locator(".session-panel-row.active")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(panel.locator(":focus")).toHaveCount(0);
  await expect(page.locator(".session-panel.pinned")).toBeVisible();
  // Unpinned under the pointer, it stays open as a hover panel until the pointer leaves.
  await page.locator(".session-panel-pin").click();
  await expect(panel).not.toHaveClass(/\bpinned\b/);
  await page.locator(".pane-area").hover();
  await expect(panel).toBeHidden();
});

test("a session waiting on approval shows amber on the rail and its request in the panel", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "feature/needs-approval");
  await submitAcpDraft(surface, "permission-lifecycle:edit-hold");
  await expect(surface.locator(".agent-entry-request")).toBeVisible();
  const waiting = page.locator(".session-chip.active");
  await expect(waiting.locator(".session-status")).toHaveClass(/\bstatus-needsInput\b/);
  const waitingSlot = await activeSessionSlot(page);

  await page.locator(".session-chip").first().click();
  await waitForSessionSwitch(page, waitingSlot);

  // A click on the rail never pops the panel open under the resting pointer; entering the rail afresh does.
  await page.waitForTimeout(400);
  await expect(page.locator(".session-panel")).toBeHidden();
  await page.locator(".pane-area").hover();
  await page.locator(".session-rail").hover();
  const needs = page.locator(".session-panel-row", { hasText: "needs approval" });
  await expect(needs).toContainText("wants to edit permission-edit.txt");
  await expect(page.locator(".session-panel-group").first()).toHaveText("Needs you");

  await needs.click();
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    waitingSlot,
  );
  await expect(page.locator(".session-panel")).toBeHidden();
});
