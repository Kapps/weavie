import { existsSync } from "node:fs";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("thinking rows stay compact, fit narrow panes, and retain manual collapse during streaming", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "thought-disclosures");
  await submitAcpDraft(
    surface,
    'mcp:setSetting:{"key":"agent.thoughtDisplay","value":"collapsed"}',
  );
  await expect(surface.locator(".agent-entry-result")).toContainText("setSetting:");
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("/clear");
  await composer.press("Escape");
  await composer.press("Enter");
  await expect(surface.locator(".agent-entry")).toHaveCount(0);
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-thought-delta"), "");
  await writeFile(join(workspace, "hold-thought-finish"), "");
  await submitAcpDraft(surface, "thought-disclosures");

  const thoughts = surface.locator(".agent-entry-thought");
  await expect(thoughts).toHaveCount(2);
  await expect(thoughts.locator("details[open]")).toHaveCount(0);
  await expect(thoughts.locator("summary")).toContainText([
    "Inspecting the transcript layout",
    "Checking narrow panes and streaming content",
  ]);
  const first = thoughts.nth(0);
  const live = thoughts.nth(1);
  await expect(first.locator("summary")).not.toContainText("**");
  await expect(live.locator("summary")).not.toContainText("###");
  const rowHeight = await first.evaluate((element) => element.getBoundingClientRect().height);
  expect(rowHeight).toBeLessThanOrEqual(32);

  // Click the empty end of the row, not just its text, and exercise both native activation keys.
  const summary = first.locator("summary");
  const bounds = (await summary.boundingBox())!;
  await summary.click({ position: { x: bounds.width - 8, y: bounds.height / 2 } });
  await expect(first.locator(".agent-markdown")).toContainText(
    "The first thought stays available for inspection.",
  );
  expect(await summary.evaluate((element) => element.getBoundingClientRect().height)).toBe(
    bounds.height,
  );
  await summary.press("Enter");
  await expect(first.locator(".agent-markdown")).toHaveCount(0);
  await summary.press("Space");
  await expect(first.locator(".agent-markdown")).toBeVisible();

  await live.locator("summary").click();
  await expect(live.locator(".agent-markdown pre")).toContainText("y".repeat(240));
  await live.locator("summary").click();
  await writeFile(join(workspace, "release-thought-delta"), "");
  await expect.poll(() => existsSync(join(workspace, "thought-finish-started"))).toBe(true);
  await expect(live.locator("details")).not.toHaveAttribute("open");
  await live.locator("summary").click();
  await expect(live.locator(".agent-markdown")).toContainText(
    "The streamed continuation remains readable.",
  );
  await live.locator("summary").click();
  await writeFile(join(workspace, "release-thought-finish"), "");
  await expect(surface.locator(".agent-entry-result")).toContainText(
    "Thought disclosure checks complete.",
  );
  await expect(live.locator("details")).not.toHaveAttribute("open");

  await createAcpSession(page, "other-thought-session");
  await page.locator('.session-chip[title^="thought-disclosures —"]').click();
  await expect(first.locator(".agent-markdown")).toBeVisible();
  await expect(live.locator("details")).not.toHaveAttribute("open");
  await first.locator("summary").click();

  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"font.size","value":28}');
  await expect
    .poll(() =>
      surface.evaluate((element) =>
        getComputedStyle(element).getPropertyValue("--editor-font-size"),
      ),
    )
    .toBe("28px");
  await first.scrollIntoViewIfNeeded();
  expect(await first.evaluate((element) => element.getBoundingClientRect().height)).toBe(rowHeight);

  await page.setViewportSize({ width: 390, height: 800 });
  await page.locator(".session-inbox-row.active .session-inbox-open").click();
  await expect(surface).toBeVisible();
  await live.scrollIntoViewIfNeeded();
  await live.locator("summary").click();
  await expect(live.locator(".agent-markdown")).toBeVisible();
  const widths = await live.evaluate((element) => ({
    row: element.clientWidth,
    rowContent: element.scrollWidth,
    body: element.closest(".agent-body")!.clientWidth,
    bodyContent: element.closest(".agent-body")!.scrollWidth,
  }));
  expect(widths.row).toBeLessThan(500);
  expect(widths.rowContent).toBeLessThanOrEqual(widths.row);
  expect(widths.bodyContent).toBeLessThanOrEqual(widths.body);
});
