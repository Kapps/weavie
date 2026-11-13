import type { Locator, Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { persistedSessions } from "../harness/persisted-sessions";

const dashboard = { id: "rollout", title: "Checkout rollout" };
const chart = (id: string) => ({
  id,
  title: `${id} chart`,
  dashboard,
  chart: {
    kind: "line",
    x: { type: "number" },
    series: [
      {
        name: "now",
        points: [
          [1, 2],
          [2, 3],
        ],
      },
    ],
  },
});

const show = (relay: string, ...ids: string[]) =>
  `${ids.length > 1 ? "block-parallel" : "block"}:${relay}:showBlock:${ids.map((id) => JSON.stringify(chart(id))).join("\n")}`;

const tab = (page: Page, name: string): Locator => page.locator(".editor-tab", { hasText: name });

test("a dashboard gathers its blocks into one card that opens in a tab and leaves the strip when collapsed", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "card-tabs-dashboard");
  await submitAcpDraft(surface, show("claude", "p95", "errors"));
  const card = surface.locator('[data-agent-dashboard="rollout"]');
  await expect(card.locator(".agent-entry-block")).toHaveCount(2);
  await expect(card.locator(".agent-aside-head")).toContainText("Dashboard · Checkout rollout");

  // A block added in a later turn joins the card and leaves a line at its own turn.
  await submitAcpDraft(surface, show("codex", "rps"));
  await expect(card.locator(".agent-entry-block")).toHaveCount(3);
  const line = surface.locator(".agent-entry-block-added");
  await expect(line).toHaveText("rps chart · added to Checkout rollout");
  await expect(surface.locator(".agent-entry-block")).toHaveCount(3);

  // Open puts the same blocks in a tab, one per row.
  await card.getByRole("button", { name: "Open", exact: true }).click();
  await expect(tab(page, "Checkout rollout")).toBeVisible();
  const view = page.locator(".agent-blocks-tab");
  await expect(view.locator(".agent-entry-block .agent-chart canvas")).toHaveCount(3);

  // The strip lists the dashboard; ✕ collapses its card, which takes it off the strip.
  const entry = surface.locator('.agent-background-tray [data-strip-card^="dashboard-rollout"]');
  await expect(entry).toContainText("Checkout rollout");
  await entry.getByRole("button", { name: "Collapse Checkout rollout" }).click();
  await expect(entry).toHaveCount(0);
  await expect(card.locator(".agent-entry-block")).toHaveCount(0);
  await expect(card.locator(".agent-aside-head small")).toHaveText("3 charts");
  await expect(view.locator(".agent-entry-block")).toHaveCount(3);

  await page.reload();
  const restored = page.locator(
    '[data-surface="structured-agent"] [data-agent-dashboard="rollout"]',
  );
  await expect(restored.locator(".agent-aside-head small")).toHaveText("3 charts");
  await restored.getByRole("button", { name: /^Expand Dashboard/ }).click();
  await expect(restored.locator(".agent-entry-block")).toHaveCount(3);
  await expect(
    page.locator('.agent-background-tray [data-strip-card^="dashboard-rollout"]'),
  ).toHaveCount(1);
});

test("a block's height in a tab is the user's to drag, and the tab keeps it", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "card-tabs-height");
  await submitAcpDraft(surface, show("claude", "p95"));
  await surface
    .locator('[data-agent-dashboard="rollout"]')
    .getByRole("button", { name: "Open", exact: true })
    .click();
  const frame = page.locator(".agent-blocks-tab .agent-entry-block").first();
  await expect(frame.locator(".agent-chart canvas")).toBeVisible();
  const before = (await frame.boundingBox())!.height;

  const handle = page.getByRole("button", { name: "Resize p95 chart" });
  const grip = (await handle.boundingBox())!;
  await page.mouse.move(grip.x + grip.width / 2, grip.y + grip.height / 2);
  await page.mouse.down();
  await page.mouse.move(grip.x + grip.width / 2, grip.y + 120, { steps: 5 });
  await page.mouse.up();
  await expect.poll(async () => (await frame.boundingBox())!.height).toBeGreaterThan(before + 100);
  const dragged = (await frame.boundingBox())!.height;
  await handle.press("ArrowDown");
  await expect.poll(async () => (await frame.boundingBox())!.height).toBe(dragged + 40);

  // The tab and its heights reach the host's saved editor session before the page reloads.
  await expect
    .poll(() => persistedSessions(weavie.home))
    .toMatch(new RegExp(`"p95":\\s*${dragged + 40}\\b`));
  await page.reload();
  const restored = page.locator(".agent-blocks-tab .agent-entry-block").first();
  await expect(restored.locator(".agent-chart canvas")).toBeVisible();
  expect((await restored.boundingBox())!.height).toBe(dragged + 40);
});

test("a lone block and a BTW open in tabs, and the BTW's reply box keeps focus where it was opened", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "card-tabs-aside");
  const lone = { ...chart("solo"), dashboard: undefined };
  await submitAcpDraft(surface, `block:claude:showBlock:${JSON.stringify(lone)}`);
  const block = surface.locator(".agent-entry-block");
  await block.getByRole("button", { name: "Open", exact: true }).click();
  await expect(tab(page, "solo chart")).toBeVisible();
  await expect(page.locator(".agent-blocks-tab .agent-chart canvas")).toBeVisible();

  await submitAcpDraft(surface, "/btw side question");
  const aside = surface.locator(".agent-aside[data-agent-aside]");
  await expect(aside.getByRole("button", { name: "Reply", exact: true })).toBeEnabled();
  await aside.getByRole("button", { name: "Open", exact: true }).click();
  await expect(tab(page, "BTW · side question")).toBeVisible();
  const view = page.locator(".editor-agent-view");
  await expect(view).toContainText("side question");

  await view.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = view.getByRole("textbox", { name: "Reply to BTW" });
  await expect(reply).toBeFocused();
  await reply.fill("and then?");
  // The chat card shows the same draft, without taking focus from the tab.
  await expect(aside.getByRole("textbox", { name: "Reply to BTW" })).toHaveValue("and then?");
  await expect(reply).toBeFocused();
});
