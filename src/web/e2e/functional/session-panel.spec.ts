import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, runCommand, waitForSessionSwitch } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// The session switcher: a rail of status dots that expands in place into the session panel (hover, Ctrl+E), the
// pinned panel, and live agent activity from the fake ACP agent.
test.use({
  fakeScript: {
    steps: [
      { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: false } },
    ],
  },
});

const expanded = ".session-rail.expanded";

test("hover and Ctrl+E expand the rail; arrows switch; pinning docks the panel across a reload", async ({
  page,
}) => {
  await createAcpSession(page, "feature/panel-second");
  const panel = page.locator(expanded);
  const rows = panel.locator(".session-chip");

  // Expanding widens the rail in place: every chip keeps its box, including a one-word name ("main").
  const chipBoxes = (): Promise<string> =>
    page
      .locator(".session-chip")
      .evaluateAll((chips) =>
        JSON.stringify(chips.map((chip) => [chip.offsetTop, chip.offsetHeight])),
      );
  const collapsed = await chipBoxes();
  await page.locator(".session-rail").hover();
  await expect(rows).toHaveCount(2);
  expect(await chipBoxes()).toBe(collapsed);
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
  // The expanded rail keeps rail order: the workspace session first.
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
  const pinned = page.locator(".session-panel");
  await expect(pinned).toBeVisible();
  expect(await left()).toBeGreaterThan(unpinned + 200);
  // Pinned, a number key under the pointer doesn't switch sessions.
  // FLAKE 2026-10-14 05:51 UTC https://github.com/Kapps/weavie/actions/runs/38517590218 (main, shard 5/6): not a flake — #1068
  // merged this block querying the unpinned rail (`panel`), which pinning removes; it now queries the pinned panel.
  const activeRow = pinned.locator(".session-panel-row.active");
  const pinnedOn = await activeRow.getAttribute("data-session-slot");
  await pinned.hover();
  await page.keyboard.press("2");
  await page.waitForTimeout(500);
  await expect(activeRow).toHaveAttribute("data-session-slot", pinnedOn ?? "");
  // Pinned, the panel replaces the rail and carries its own new-session button.
  await expect(page.locator(".session-rail")).toHaveCount(0);
  await expect(pinned.locator(".session-rail-add")).toBeVisible();
  // Rows keep the rail's right-click session menu.
  await pinned.locator(".session-panel-row").first().click({ button: "right" });
  await expect(page.locator(".context-menu")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.locator(".context-menu")).toBeHidden();

  // Dragging the right edge resizes it, and the width survives a reload.
  const edge = await pinned.locator(".session-panel-resize").boundingBox();
  if (edge === null) throw new Error("no resize edge");
  await page.mouse.move(edge.x + edge.width / 2, edge.y + 200);
  await page.mouse.down();
  await page.mouse.move(edge.x + edge.width / 2 + 80, edge.y + 200, { steps: 5 });
  await page.mouse.up();
  const resized = (await pinned.boundingBox())?.width ?? 0;
  expect(resized).toBeGreaterThan(360);

  await page.reload();
  await expect(pinned).toBeVisible();
  expect((await pinned.boundingBox())?.width ?? 0).toBeCloseTo(resized, 0);
  // Pinned, Escape hands focus back instead of closing anything.
  await page.locator(".xterm:visible").first().click();
  await page.keyboard.press("ControlOrMeta+e");
  await expect(pinned.locator(".session-panel-row.active")).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(pinned.locator(":focus")).toHaveCount(0);
  await expect(pinned).toBeVisible();
  // Unpinned under the pointer, the rail stays expanded until the pointer leaves.
  await page.locator(".session-panel-pin").click();
  await expect(pinned).toBeHidden();
  await expect(panel).toBeVisible();
  await page.locator(".pane-area").hover();
  await expect(panel).toBeHidden();
});

test("a session waiting on approval shows amber on the rail and its request when expanded", async ({
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
  await expect(page.locator(expanded)).toBeHidden();
  await page.locator(".pane-area").hover();
  await page.locator(".session-rail").hover();
  const needs = page.locator(`${expanded} .session-chip`, { hasText: "needs approval" });
  await expect(needs).toContainText("wants to edit permission-edit.txt");
  await runCommand(page, "Pin Session Panel");
  await expect(page.locator(".session-panel-group").first()).toHaveText("Needs you");
  await runCommand(page, "Pin Session Panel");
  await page.locator(".pane-area").hover();
  await page.locator(".session-rail").hover();

  await needs.click();
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    waitingSlot,
  );
  await expect(page.locator(expanded)).toBeHidden();
});

test("parked sessions fold behind one toggle shared by the rail and the pinned panel", async ({
  page,
}) => {
  await createAcpSession(page, "feature/parked-fold");
  await runCommand(page, "Unload Session");
  const toggle = page.locator(".session-rail-parked");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);

  await toggle.click();
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);

  await runCommand(page, "Pin Session Panel");
  const parkedHeader = page.locator(".session-panel-parked");
  await expect(parkedHeader).toHaveAttribute("aria-expanded", "true");
  await expect(page.locator(".session-panel-row.parked")).toHaveCount(1);
  await parkedHeader.click();
  await expect(page.locator(".session-panel-row.parked")).toHaveCount(0);
  await runCommand(page, "Pin Session Panel");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
});
