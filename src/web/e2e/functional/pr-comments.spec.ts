import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";

// Inline PR review threads: cards in a layer beside Monaco (not view-zone DOM), sized to the visible code,
// with keyboard reply / new comment / edit round-tripping through the stubbed comment store.
test.use({ prScenario: true });

const card = (page: Page, text: string) =>
  page.locator(".weavie-pr-card").filter({ hasText: text });

async function openCommentedFile(page: Page): Promise<void> {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  await expect(card(page, "Why change this greeting?")).toBeVisible({ timeout: 10_000 });
}

// Puts the caret on `line` of the focused editor.
async function goToLine(page: Page, line: number): Promise<void> {
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  for (let i = 1; i < line; i++) await page.keyboard.press("ArrowDown");
}

test("a thread fits the visible editor and reserves its own space below its line", async ({
  page,
}) => {
  await openCommentedFile(page);
  const thread = card(page, "Why change this greeting?");
  const editor = await page.locator(".monaco-editor").first().boundingBox();
  const box = await thread.boundingBox();
  expect(editor).not.toBeNull();
  expect(box).not.toBeNull();
  if (editor && box) {
    expect(box.x).toBeGreaterThan(editor.x);
    expect(box.x + box.width).toBeLessThanOrEqual(editor.x + editor.width);
  }
  // The card sits below line 2 — the code after it is pushed down, never painted over.
  const below = page.locator(".monaco-editor .view-line", { hasText: "const message" }).first();
  await expect
    .poll(async () => {
      const card = await thread.boundingBox();
      return ((await below.boundingBox())?.y ?? 0) - ((card?.y ?? 0) + (card?.height ?? 0));
    })
    .toBeGreaterThanOrEqual(0);
});

test("reply from the keyboard: the card grows and the reply renders", async ({ page }) => {
  await openCommentedFile(page);
  const thread = card(page, "Why change this greeting?");
  const before = (await thread.boundingBox())?.height ?? 0;
  await thread.locator(".weavie-pr-reply-stub").click();
  const input = thread.locator(".weavie-pr-composer-input");
  await expect(input).toBeFocused();
  await input.fill("Addressed in the latest push.");
  await input.press("ControlOrMeta+Enter");
  await expect(
    thread.locator(".weavie-pr-comment-body", { hasText: "Addressed in the latest push." }),
  ).toBeVisible({ timeout: 10_000 });
  await expect(thread.locator(".weavie-pr-composer-input")).toHaveCount(0);
  // The review's hunk is untouched: Ctrl+Enter submitted the comment instead of Keeping the change.
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();
  expect((await thread.boundingBox())?.height ?? 0).toBeGreaterThan(before);
});

test("a new comment on a PR line posts, then its author can edit it", async ({ page }) => {
  await openCommentedFile(page);
  await goToLine(page, 5);
  await runCommand(page, "Comment on Line (Pull Request)");
  const draft = page.locator(".weavie-pr-card", { hasText: "New comment on line 5" });
  const input = draft.locator(".weavie-pr-composer-input");
  await expect(input).toBeFocused();
  await input.fill("Nit: keep the period.");
  await input.press("ControlOrMeta+Enter");
  const posted = card(page, "Nit: keep the period.");
  await expect(posted).toBeVisible({ timeout: 10_000 });
  await expect(draft).toHaveCount(0);

  await posted.locator(".weavie-pr-comment").hover();
  await posted.getByRole("button", { name: "Edit comment" }).click();
  // Editing swaps the body for the composer, so the card is found by its (only) open comment box.
  const edit = page.locator(".weavie-pr-card .weavie-pr-composer-input");
  await expect(edit).toHaveValue("Nit: keep the period.");
  await edit.fill("Nit: keep the trailing period.");
  await edit.press("ControlOrMeta+Enter");
  await expect(card(page, "Nit: keep the trailing period.")).toBeVisible({ timeout: 10_000 });
  // Someone else's comment offers no edit.
  await expect(
    card(page, "Why change this greeting?").getByRole("button", { name: "Edit comment" }),
  ).toHaveCount(0);
});

test("Escape cancels a new comment without touching the review", async ({ page }) => {
  await openCommentedFile(page);
  await goToLine(page, 5);
  await runCommand(page, "Comment on Line (Pull Request)");
  const input = page.locator(".weavie-pr-card .weavie-pr-composer-input");
  await expect(input).toBeFocused();
  await input.press("Escape");
  await expect(page.locator(".weavie-pr-card", { hasText: "New comment" })).toHaveCount(0);
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible();
});

test("threads show in the plain editor once the review is closed", async ({ page }) => {
  await openCommentedFile(page);
  await runCommand(page, "Close Diff");
  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0, { timeout: 10_000 });
  await expect(card(page, "Why change this greeting?")).toBeVisible();
});
