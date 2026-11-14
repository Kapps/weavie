import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession, setFakeAcpMode, submitAcpDraft } from "../harness/acp-session";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { focusEditor } from "../harness/navigator";
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

const focused = (page: Page): Promise<string | undefined> =>
  page.evaluate(() => document.activeElement?.outerHTML.slice(0, 200));

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
  const entry = surface.locator('.agent-strip [data-strip-card^="dashboard-rollout"]');
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
  await expect(page.locator('.agent-strip [data-strip-card^="dashboard-rollout"]')).toHaveCount(1);
});

test("an overflowing strip fades the edge hiding cards, and roving focus scrolls them into view", async ({
  page,
}) => {
  await page.setViewportSize({ width: 900, height: 720 });
  const surface = await createAcpSession(page, "card-tabs-overflow");
  const cards = surface.locator(".agent-strip [data-strip-card]");
  for (let count = 1; count <= 6; count++) {
    await submitAcpDraft(surface, `/btw side question number ${count}`);
    await expect(cards).toHaveCount(count);
  }
  const scroller = surface.locator(".agent-strip .agent-status-scroll");
  await expect(scroller).toHaveClass(/overflow-right/);
  await expect(scroller).not.toHaveClass(/overflow-left/);

  await runCommand(page, "Focus Agent Strip");
  await expect(surface.locator(".agent-strip button").first()).toBeFocused();
  await page.keyboard.press("End");
  const last = cards.last().getByRole("button", { name: /^Collapse / });
  await expect(last).toBeFocused();
  await expect(scroller).toHaveClass(/overflow-left/);
  await expect(scroller).not.toHaveClass(/overflow-right/);
  const inside = await last.evaluate((button) => {
    const pill = button.getBoundingClientRect();
    const view = button.closest(".agent-status-scroll")!.getBoundingClientRect();
    return pill.left >= view.left && pill.right <= view.right;
  });
  expect(inside).toBe(true);
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

  // Clearing the conversation leaves the tab open, saying its dashboard is gone.
  const composer = page.locator('[data-surface="structured-agent"] [data-agent-composer] textarea');
  await composer.fill("/clear");
  await composer.press("Escape");
  await composer.press("Enter");
  await expect(page.locator(".agent-blocks-tab")).toHaveText(
    "This dashboard is no longer in the transcript.",
  );
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

  // Once that reply is sent, a Reply pressed in the chat card takes focus there, not in the tab.
  await reply.press("Enter");
  await expect(aside).toContainText("echo: and then?");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(aside.getByRole("textbox", { name: "Reply to BTW" })).toBeFocused();
});

test("a dashboard inside a BTW is the card Alt+B acts on once pressed", async ({ page }) => {
  const surface = await createAcpSession(page, "card-tabs-nested");
  await submitAcpDraft(surface, `/btw ${show("claude", "p95")}`);
  const aside = surface.locator(".agent-aside[data-agent-aside]");
  const nested = aside.locator('[data-agent-dashboard="rollout"]');
  await expect(nested.locator(".agent-entry-block")).toHaveCount(1);

  await nested.getByRole("button", { name: /^Collapse Dashboard/ }).click();
  await expect(nested.locator(".agent-entry-block")).toHaveCount(0);
  await page.keyboard.press("Alt+b");
  await expect(nested.locator(".agent-entry-block")).toHaveCount(1);
  await expect(aside.getByRole("button", { name: "Collapse BTW", exact: true })).toBeVisible();
});

test("an agent's background open of its dashboard keeps the editor and the keyboard where they were", async ({
  page,
  weavie,
}) => {
  // Without steering the open queues behind the held turn, so it runs while the user is typing in a file.
  await setFakeAcpMode(page, weavie.home, "no-steering");
  const surface = await createAcpSession(page, "card-tabs-background");
  await submitAcpDraft(surface, show("claude", "p95"));
  await expect(
    surface.locator('[data-agent-dashboard="rollout"] .agent-chart canvas'),
  ).toBeVisible();
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(worktree, "notes.md"), "# notes\n");
  await openFile(page, "notes.md");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await submitAcpDraft(surface, `mcp:openDashboard:${JSON.stringify({ id: "rollout" })}`);
  await expect(surface.locator(".agent-compose-queued")).toContainText("openDashboard");
  await focusEditor(page);
  const typing = await focused(page);
  await writeFile(join(worktree, "release-hold"), "");

  await expect(tab(page, "Checkout rollout")).toBeVisible();
  await expect(page.locator(".editor-tab.active")).toContainText("notes.md");
  expect(await focused(page)).toBe(typing);

  // Asked to show it, the agent's open brings the tab forward.
  await submitAcpDraft(
    surface,
    `mcp:openDashboard:${JSON.stringify({ id: "rollout", focus: true })}`,
  );
  await expect(page.locator(".editor-tab.active")).toContainText("Checkout rollout");
});

test("Alt+B over a chart in a BTW collapses the BTW, and an unsized table keeps its own height in a tab", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "card-tabs-lone");
  const lone = { ...chart("solo"), dashboard: undefined };
  await submitAcpDraft(surface, `/btw block:claude:showBlock:${JSON.stringify(lone)}`);
  const aside = surface.locator(".agent-aside[data-agent-aside]");
  const block = aside.locator(".agent-entry-block");
  await expect(block.locator(".agent-chart canvas")).toBeVisible();
  await block.locator(".agent-block-title").click();
  await page.keyboard.press("Alt+b");
  await expect(aside.getByRole("button", { name: "Expand BTW", exact: true })).toBeVisible();

  const table = {
    id: "errors",
    title: "Recent errors",
    table: {
      columns: [{ name: "message", type: "text" }],
      rows: [["one"], ["two"], ["three"]],
    },
  };
  await submitAcpDraft(surface, `block:codex:showBlock:${JSON.stringify(table)}`);
  const row = surface.locator('.agent-entry-block[data-block="table"]');
  const inChat = (await row.boundingBox())!.height;
  await row.getByRole("button", { name: "Open", exact: true }).click();
  const inTab = page.locator('.agent-blocks-tab .agent-entry-block[data-block="table"]');
  await expect(inTab.locator(".agent-table-row")).toHaveCount(3);
  expect((await inTab.boundingBox())!.height).toBe(inChat);
  // A block in its own tab has nothing further to open.
  await expect(inTab.getByRole("button", { name: "Open", exact: true })).toHaveCount(0);
});
