import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, runCommand, waitForSessionSwitch } from "../harness/actions";
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

  // With the panel open, a number key switches to that session; with it closed, the key types as usual.
  const onFirst = await activeSessionSlot(page);
  await page.keyboard.press("ControlOrMeta+e");
  await page.keyboard.press("2");
  await waitForSessionSwitch(page, onFirst);
  await expect(panel).toBeHidden();
  await expect(page.locator(".session-chip").nth(1)).toHaveClass(/\bactive\b/);
  const onSecond = await activeSessionSlot(page);
  await page.locator(".session-rail").hover();
  await expect(panel).toBeVisible();
  await page.keyboard.press("1");
  await waitForSessionSwitch(page, onSecond);
  await page.locator(".pane-area").hover();
  await expect(panel).toBeHidden();
  await page.keyboard.press("2");
  // A switch would land within a frame or two; give it room before asserting none did.
  await page.waitForTimeout(500);
  await expect(page.locator(".session-chip").first()).toHaveClass(/\bactive\b/);

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
  // Pinned, a number key under the pointer doesn't switch sessions.
  const activeRow = panel.locator(".session-panel-row.active");
  const pinnedOn = await activeRow.getAttribute("data-session-slot");
  await panel.hover();
  await page.keyboard.press("2");
  await page.waitForTimeout(500);
  await expect(activeRow).toHaveAttribute("data-session-slot", pinnedOn ?? "");
  // Pinned, the panel replaces the rail and carries its own new-session button.
  await expect(page.locator(".session-rail")).toHaveCount(0);
  await expect(panel.locator(".session-rail-add")).toBeVisible();
  // Rows keep the rail's right-click session menu.
  await panel.locator(".session-panel-row").first().click({ button: "right" });
  await expect(page.locator(".context-menu")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.locator(".context-menu")).toBeHidden();

  // Dragging the right edge resizes it, and the width survives a reload.
  const edge = await panel.locator(".session-panel-resize").boundingBox();
  if (edge === null) throw new Error("no resize edge");
  await page.mouse.move(edge.x + edge.width / 2, edge.y + 200);
  await page.mouse.down();
  await page.mouse.move(edge.x + edge.width / 2 + 80, edge.y + 200, { steps: 5 });
  await page.mouse.up();
  const resized = (await panel.boundingBox())?.width ?? 0;
  expect(resized).toBeGreaterThan(360);

  await page.reload();
  await expect(page.locator(".session-panel.pinned")).toBeVisible();
  expect((await panel.boundingBox())?.width ?? 0).toBeCloseTo(resized, 0);
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

test("parked sessions fold behind one toggle shared by the rail and the panel", async ({
  page,
}) => {
  await createAcpSession(page, "feature/parked-fold");
  await runCommand(page, "Unload Session");
  const toggle = page.locator(".session-rail-parked");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);

  await toggle.click();
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);

  await page.locator(".pane-area").hover();
  await page.locator(".session-rail").hover();
  const parkedHeader = page.locator(".session-panel-parked");
  await expect(parkedHeader).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator(".session-panel-row.parked")).toHaveCount(1);
  await parkedHeader.click();
  await expect(page.locator(".session-panel-row.parked")).toHaveCount(0);
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
});
