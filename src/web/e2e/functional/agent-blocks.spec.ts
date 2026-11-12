import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

const chart = {
  id: "p95",
  title: "p95 latency",
  chart: {
    kind: "line",
    x: { type: "time" },
    y: { unit: "ms" },
    series: [
      {
        name: "now",
        points: [
          [1794490680000, 318],
          [1794490740000, 344],
        ],
      },
      {
        name: "24h ago",
        style: "comparison",
        points: [
          [1794490680000, 297],
          [1794490740000, 262],
        ],
      },
    ],
    thresholds: [{ label: "threshold", value: 400 }],
  },
};

const table = {
  id: "errors",
  title: "Recent errors",
  table: {
    columns: [
      { name: "time", type: "time" },
      { name: "level", type: "level" },
      { name: "message", type: "text" },
    ],
    rows: [
      ["2026-11-12T14:31:02Z", "error", "upstream timed out"],
      ["2026-11-12T14:30:40Z", "info", "deploy started"],
      ["2026-11-12T14:31:10Z", "warn", "retrying"],
    ],
  },
};

test("an agent's chart sits between its reply text, keeps its height, and updates in place", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "agent-blocks-chart");

  await submitAcpDraft(surface, `block:claude:showBlock:${JSON.stringify(chart)}`);

  const block = surface.locator(".agent-entry-block");
  await expect(block).toHaveCount(1);
  await expect(block.locator(".agent-block-title")).toHaveText("p95 latency");
  await expect(block.locator(".agent-chart canvas")).toBeVisible();
  expect(await block.evaluate((element) => element.getBoundingClientRect().height)).toBe(300);
  const order = await surface
    .locator(".agent-entry-message.agent-tone-assistant, .agent-entry-block")
    .allTextContents();
  const shown = order.findIndex((text) => text.includes("p95 latency"));
  expect(order[shown - 1]).toContain("block call starting");
  expect(order[shown + 1]).toContain("block call done");

  // Several small appends in one turn, their results reported out of order.
  const appends = [371, 389, 402].map((value) =>
    JSON.stringify({
      id: "p95",
      append: { series: [{ name: "now", points: [[1794490800000 + value, value]] }] },
      keep: 60,
    }),
  );
  await submitAcpDraft(surface, `block-parallel:codex:updateBlock:${appends.join("\n")}`);

  await expect(block.locator(".agent-entry-status")).toContainText("updated");
  await expect(surface.locator(".agent-entry-block")).toHaveCount(1);
  expect(await block.evaluate((element) => element.getBoundingClientRect().height)).toBe(300);

  await page.reload();
  const restored = page.locator('[data-surface="structured-agent"] .agent-entry-block');
  await expect(restored.locator(".agent-chart canvas")).toBeVisible();
  await expect(restored.locator(".agent-entry-status")).toContainText("updated");

  // Dragging across the chart attaches that range to the next prompt.
  await restored.scrollIntoViewIfNeeded();
  const plot = (await restored.locator(".agent-chart").boundingBox())!;
  await page.mouse.move(plot.x + plot.width * 0.2, plot.y + plot.height * 0.5);
  await page.mouse.down();
  await page.mouse.move(plot.x + plot.width * 0.95, plot.y + plot.height * 0.5, { steps: 6 });
  await page.mouse.up();
  const chip = page.locator(
    '[data-surface="structured-agent"] [data-agent-composer] .agent-attachment-selection',
  );
  await expect(chip).toHaveCount(1);
  await expect(chip).toContainText("p95 latency ·");
});

test("an agent's table sorts, filters, and attaches dragged cells", async ({ page }) => {
  const surface = await createAcpSession(page, "agent-blocks-table");

  await submitAcpDraft(surface, `block:codex:showBlock:${JSON.stringify(table)}`);

  const block = surface.locator(".agent-entry-block");
  const messages = block.locator(".agent-table-row .agent-table-cell:nth-of-type(4)");
  await expect(messages).toHaveText(["upstream timed out", "deploy started", "retrying"]);
  await expect(block.locator('[data-level="bad"]')).toHaveText("error");

  await block.getByRole("button", { name: "time" }).click();
  await expect(messages).toHaveText(["deploy started", "upstream timed out", "retrying"]);

  await block.getByRole("searchbox", { name: "Filter Recent errors" }).fill("retry");
  await expect(messages).toHaveText(["retrying"]);
  await block.getByRole("searchbox", { name: "Filter Recent errors" }).fill("");

  const first = block.locator('[data-cell="0:1"]');
  const last = block.locator('[data-cell="1:2"]');
  await first.hover();
  await page.mouse.down();
  await last.hover();
  await page.mouse.up();
  const chip = surface.locator("[data-agent-composer] .agent-attachment-selection");
  await expect(chip).toHaveCount(1);
  await expect(chip).toContainText("Recent errors · rows 1-2");
});

test("an agent that can't report a block's result says it wasn't shown", async ({ page }) => {
  const surface = await createAcpSession(page, "agent-blocks-unreported");

  await submitAcpDraft(surface, `block:none:showBlock:${JSON.stringify(chart)}`);

  await expect(surface.locator(".agent-entry-notice")).toContainText(
    "did not report the result of showBlock, so block 'p95' was not shown.",
  );
  await expect(surface.locator(".agent-entry-block")).toHaveCount(0);
});
