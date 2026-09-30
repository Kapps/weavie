import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";

// Video tour of inline PR review threads: fit, reply, new comment, edit, and threads surviving Close Diff.
test.use({ prScenario: true });
test.setTimeout(120_000);

const hold = (page: Page, ms: number) => page.waitForTimeout(ms);
const card = (page: Page, text: string) =>
  page.locator(".weavie-pr-card").filter({ hasText: text });
const shots = "e2e/.recordings";
// The sandbox has no TS language server; its "unavailable" toasts would cover the card on camera.
async function dismissToasts(page: Page): Promise<void> {
  const close = page.locator(".toast:not(.leaving) .toast-close");
  while ((await close.count()) > 0) await close.first().click();
  await expect(page.locator(".toast")).toHaveCount(0);
}

test("PR comments: inline cards, reply, new comment, edit, close diff", async ({ page }) => {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await hold(page, 1200);
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await hold(page, 800);

  // (1) Thread card inline under line 2, clamped to the visible editor.
  await walkToChangedFile(page, "hello.ts");
  const thread = card(page, "Why change this greeting?");
  await expect(thread).toBeVisible({ timeout: 10_000 });
  const editor = await page.locator(".monaco-editor").first().boundingBox();
  const box = await thread.boundingBox();
  if (!editor || !box) throw new Error("missing boxes");
  console.log("editor", JSON.stringify(editor), "card", JSON.stringify(box));
  expect(box.x + box.width).toBeLessThanOrEqual(editor.x + editor.width);
  const ruler = await page
    .locator(".monaco-editor .decorationsOverviewRuler")
    .first()
    .boundingBox();
  if (!ruler) throw new Error("missing overview ruler");
  console.log("card right", box.x + box.width, "ruler left", ruler.x);
  expect(box.x + box.width).toBeLessThan(ruler.x);
  await expect(thread.locator(".weavie-pr-card-title")).toHaveText("1 comment");
  const overflow = await thread.evaluate((el) => el.scrollWidth - el.clientWidth);
  console.log("card horizontal overflow px", overflow);
  expect(overflow).toBeLessThanOrEqual(0);
  await hold(page, 800);
  await dismissToasts(page);
  await hold(page, 1500);
  await page.screenshot({ path: `${shots}/pr-comments-editor.png` });
  await thread.screenshot({ path: `${shots}/pr-comments-card.png` });

  // (2) Hover a comment, reply via the Reply… box + Ctrl+Enter.
  await thread.locator(".weavie-pr-comment").first().hover();
  await hold(page, 1200);
  await thread.locator(".weavie-pr-reply-stub").click();
  const reply = thread.locator(".weavie-pr-composer-input");
  await expect(reply).toBeFocused();
  await reply.pressSequentially("Addressed in the latest push.", { delay: 40 });
  await hold(page, 800);
  await thread.screenshot({ path: `${shots}/pr-comments-composer.png` });
  await reply.press("ControlOrMeta+Enter");
  await expect(
    thread.locator(".weavie-pr-comment-body", { hasText: "Addressed in the latest push." }),
  ).toBeVisible({ timeout: 10_000 });
  await hold(page, 1800);
  await expect(thread.locator(".weavie-pr-card-title")).toHaveText("2 comments");
  await thread.screenshot({ path: `${shots}/pr-comments-replied.png` });

  // (3) Caret on line 5 → Comment on Line (Pull Request).
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  for (let i = 1; i < 5; i++) await page.keyboard.press("ArrowDown");
  await hold(page, 600);
  await runCommand(page, "Comment on Line (Pull Request)");
  const draft = card(page, "New comment on line 5");
  const input = draft.locator(".weavie-pr-composer-input");
  await expect(input).toBeFocused();
  await hold(page, 800);
  await input.pressSequentially("Nit: keep the period.", { delay: 40 });
  await hold(page, 600);
  await input.press("ControlOrMeta+Enter");
  const posted = card(page, "Nit: keep the period.");
  await expect(posted).toBeVisible({ timeout: 10_000 });
  await hold(page, 1800);

  // (4) Pencil Edit on own comment → change → save → "edited".
  await posted.locator(".weavie-pr-comment").hover();
  await hold(page, 900);
  await posted.getByRole("button", { name: "Edit comment" }).click();
  const edit = page.locator(".weavie-pr-card .weavie-pr-composer-input");
  await expect(edit).toHaveValue("Nit: keep the period.");
  const editing = page
    .locator(".weavie-pr-card")
    .filter({ has: page.locator(".weavie-pr-comment .weavie-pr-composer-input") });
  await expect(editing).toHaveCount(1);
  await expect(editing.locator(".weavie-pr-reply-stub")).toBeHidden();
  await editing.screenshot({ path: `${shots}/pr-comments-editing.png` });
  await hold(page, 700);
  await edit.press("End");
  await edit.press("ArrowLeft");
  await edit.pressSequentially(" at the end", { delay: 50 });
  await hold(page, 700);
  await edit.press("ControlOrMeta+Enter");
  const edited = card(page, "Nit: keep the period at the end.");
  await expect(edited).toBeVisible({ timeout: 10_000 });
  await expect(edited).toContainText("edited");
  await expect(edited.locator(".weavie-pr-reply-stub")).toBeVisible();
  await hold(page, 1800);
  await edited.screenshot({ path: `${shots}/pr-comments-edited.png` });
  await page.screenshot({ path: `${shots}/pr-comments-two-threads.png` });

  // (5) Close Diff → threads still show in the plain editor.
  await dismissToasts(page);
  await runCommand(page, "Close Diff");
  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0, { timeout: 10_000 });
  await expect(card(page, "Why change this greeting?")).toBeVisible();
  await expect(card(page, "Nit: keep the period at the end.")).toBeVisible();
  await hold(page, 800);
  await dismissToasts(page);
  await hold(page, 2000);
  await page.screenshot({ path: `${shots}/pr-comments-closed-diff.png` });
});
