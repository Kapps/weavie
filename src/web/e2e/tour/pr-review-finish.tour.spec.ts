import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";

// Video tour of finishing a PR review: queue a comment and a reply as pending drafts, then submit once.
test.use({ prScenario: true });
test.setTimeout(150_000);

const hold = (page: Page, ms: number) => page.waitForTimeout(ms);
const card = (page: Page, text: string) =>
  page.locator(".weavie-pr-card").filter({ hasText: text });
const shots = "e2e/.recordings";
async function dismissToasts(page: Page): Promise<void> {
  const close = page.locator(".toast:not(.leaving) .toast-close");
  while ((await close.count()) > 0) await close.first().click();
  await expect(page.locator(".toast")).toHaveCount(0);
}

test("Finish a review: pending drafts in place, then submit with a verdict", async ({ page }) => {
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  page.on("pageerror", (e) => errors.push(`[pageerror] ${String(e)}`));

  // (1) Open PR #101 and walk to hello.ts; the existing thread shows.
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await hold(page, 1200);
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  const thread = card(page, "Why change this greeting?");
  await expect(thread).toBeVisible({ timeout: 10_000 });
  await dismissToasts(page);
  await hold(page, 1500);

  // (2) Comment on line 6: composer offers Start review + Comment now; Start review → pending card.
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  for (let i = 1; i < 6; i++) await page.keyboard.press("ArrowDown");
  await hold(page, 600);
  await runCommand(page, "Comment on Line (Pull Request)");
  const draft = page.locator(".weavie-pr-card-draft");
  const start = draft.getByRole("button", { name: "Start review", exact: true });
  await expect(start).toBeVisible();
  await expect(draft.getByRole("button", { name: "Comment now", exact: true })).toBeVisible();
  console.log("start review title:", await start.getAttribute("title"));
  await expect(start).toHaveAttribute("title", /Ctrl\+Enter/);
  const input = draft.locator(".weavie-pr-composer-input");
  await input.pressSequentially("Return early here instead of nesting.", { delay: 40 });
  await hold(page, 800);
  await draft.screenshot({ path: `${shots}/finish-review-composer.png` });
  await start.hover();
  await hold(page, 1000);
  await start.click();
  const queued = card(page, "Return early here instead of nesting.");
  await expect(queued).toBeVisible({ timeout: 10_000 });
  await expect(queued).toHaveClass(/weavie-pr-card-pending/);
  await expect(queued.locator(".weavie-pr-badge-pending")).toHaveText("Pending");
  const outline = await queued.evaluate((el) => getComputedStyle(el).borderStyle);
  console.log("pending card border-style:", outline);
  await hold(page, 1800);
  await queued.screenshot({ path: `${shots}/finish-review-pending-card.png` });

  // (3) Reply to the existing thread: Add to review + pending note, no Comment now.
  await thread.locator(".weavie-pr-reply-stub").click();
  const reply = thread.locator(".weavie-pr-composer-input");
  await expect(reply).toBeFocused();
  await expect(thread.getByRole("button", { name: "Add to review", exact: true })).toBeVisible();
  await expect(thread.getByRole("button", { name: "Comment now" })).toHaveCount(0);
  await expect(thread.locator(".weavie-pr-composer-note")).toHaveText("Review pending · 1 comment");
  await reply.pressSequentially("Kept it for the release notes.", { delay: 40 });
  await hold(page, 1000);
  await thread.screenshot({ path: `${shots}/finish-review-reply-composer.png` });
  await reply.press("ControlOrMeta+Enter");
  await expect(
    thread.locator(".weavie-pr-comment-draft", { hasText: "Kept it for the release notes." }),
  ).toBeVisible({ timeout: 10_000 });
  await hold(page, 1800);
  await thread.screenshot({ path: `${shots}/finish-review-pending-reply.png` });

  // (4) Review Changes → Finish review (2) → panel.
  await dismissToasts(page);
  await runCommand(page, "Review Changes");
  const button = page.locator(".finish-review-button");
  await expect(button).toContainText("Finish review");
  await expect(button.locator(".finish-review-count")).toHaveText("2");
  await hold(page, 1200);
  await button.hover();
  await hold(page, 1000);
  await button.click();
  const panel = page.locator(".finish-review-panel");
  await expect(panel).toBeVisible();
  const verdicts = panel.locator(".finish-review-verdict");
  await expect(verdicts).toHaveCount(3);
  console.log("verdicts:", JSON.stringify(await verdicts.allInnerTexts()));
  const drafts = panel.locator(".finish-review-draft");
  await expect(drafts).toHaveCount(2);
  console.log("drafts:", JSON.stringify(await drafts.allInnerTexts()));
  await hold(page, 2000);
  await page.screenshot({ path: `${shots}/finish-review-panel.png` });

  // Jump to a draft, then reopen the panel.
  await drafts.filter({ hasText: "hello.ts:6" }).click();
  await hold(page, 1800);
  console.log("panel open after jump:", await panel.isVisible());
  console.log("finish-review button count after jump:", await button.count());
  console.log(
    "inline toolbar count after jump:",
    await page.locator(".weavie-inline-toolbar").count(),
  );
  await page.screenshot({ path: `${shots}/finish-review-after-jump.png` });
  // The jump leaves the review view; Finish Review… reopens it with the panel.
  await runCommand(page, "Finish Review…");
  await expect(panel).toBeVisible({ timeout: 10_000 });
  await hold(page, 1200);

  const summary = panel.locator("textarea");
  await summary.click();
  await summary.pressSequentially("Two small things inline; otherwise good.", { delay: 40 });
  await hold(page, 700);
  await panel.getByText("Request changes", { exact: true }).click();
  await hold(page, 1200);
  await page.screenshot({ path: `${shots}/finish-review-panel-filled.png` });
  await summary.press("ControlOrMeta+Enter");

  await expect(panel).toHaveCount(0, { timeout: 10_000 });
  await expect(button).toHaveText("Review");
  await hold(page, 1500);
  await expect(card(page, "Return early here instead of nesting.")).toBeVisible({
    timeout: 10_000,
  });
  await expect(page.locator(".weavie-pr-badge-pending")).toHaveCount(0);
  await expect(page.locator(".weavie-pr-card-pending")).toHaveCount(0);
  await expect(page.locator(".weavie-pr-comment-draft")).toHaveCount(0);
  await dismissToasts(page);
  await hold(page, 2200);
  await page.screenshot({ path: `${shots}/finish-review-submitted.png` });

  console.log("console errors:", JSON.stringify(errors));
  expect(errors).toEqual([]);
});
