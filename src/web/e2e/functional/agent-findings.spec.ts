import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

const pin = (title: string, detail?: string): string =>
  `block:claude:pinFinding:${JSON.stringify({ id: "cause", title, ...(detail ? { detail } : {}) })}`;

test("a pinned finding sits on the board, shows where the reply places it, and is replaced in place", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "agent-findings");
  const board = surface.locator("[data-agent-findings]");

  await submitAcpDraft(
    surface,
    pin("The review opens with no merge check.", "`ReadOperationAsync` has **one** caller."),
  );

  const pinned = board.locator(".agent-finding-row");
  await expect(pinned).toHaveText("The review opens with no merge check.");
  await expect(board.locator(".agent-roster-toggle")).toHaveText("Findings1");
  // The pin is never a transcript row: not as a tool, and not as a block.
  await expect(surface.locator(".agent-entry-block")).toHaveCount(0);
  await expect(
    surface.locator(".agent-entry:not(.agent-tone-user)").getByText("pinFinding"),
  ).toHaveCount(0);
  await pinned.click();
  await expect(board.locator(".agent-disclosure-body strong")).toHaveText("one");

  await submitAcpDraft(
    surface,
    "say:The cause:\n\n[[ca|use]]\n\nand an id nobody pinned:\n\n[[no|pe]]",
  );

  const reply = surface
    .locator(".agent-entry-message.agent-tone-assistant")
    .filter({ has: page.locator(".agent-finding-slot") });
  const placed = reply.locator(".agent-finding-slot .agent-finding-row");
  await expect(placed).toHaveText("The review opens with no merge check.");
  await expect(reply.getByText("[[nope]]", { exact: true })).toBeVisible();
  await expect(reply.getByText("[[cause]]")).toHaveCount(0);
  // The reply's copy opens on its own.
  await expect(reply.locator(".agent-disclosure-body")).toHaveCount(0);
  await placed.click();
  await expect(reply.locator(".agent-disclosure-body strong")).toHaveText("one");

  await submitAcpDraft(surface, pin("The review checks for a merge, but too late."));

  await expect(pinned).toContainText("The review checks for a merge, but too late.");
  await expect(pinned).toContainText("updated");
  await expect(placed).toContainText("The review checks for a merge, but too late.");
  await expect(board.locator(".agent-roster-toggle")).toHaveText("Findings1");

  await page.reload();
  const reloaded = page.locator('[data-surface="structured-agent"]');
  await expect(reloaded.locator("[data-agent-findings] .agent-finding-row")).toContainText(
    "The review checks for a merge, but too late.",
  );
  await expect(reloaded.locator(".agent-finding-slot .agent-finding-row")).toContainText(
    "The review checks for a merge, but too late.",
  );
});

test("Expand or Collapse Findings hides the board's rows and keeps its count", async ({ page }) => {
  const surface = await createAcpSession(page, "agent-findings-toggle");
  await submitAcpDraft(surface, pin("It races."));
  const board = surface.locator("[data-agent-findings]");
  await expect(board.locator(".agent-finding-row")).toHaveCount(1);

  await board.locator(".agent-roster-toggle").click();

  await expect(board.locator(".agent-finding-row")).toHaveCount(0);
  await expect(board.locator(".agent-roster-toggle")).toHaveAttribute("aria-expanded", "false");
  await expect(board.locator(".agent-roster-toggle")).toHaveText("Findings1");
});
