import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

async function askAboutLine(page: Page, line: number, question: string): Promise<void> {
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+Home");
  for (let step = 1; step < line; step += 1) await page.keyboard.press("ArrowDown");
  await page.keyboard.press("Shift+End");
  await page.getByRole("button", { name: "Comment on Selection", exact: true }).click();
  const comment = page
    .getByRole("dialog", { name: "Comment on Selection" })
    .getByRole("textbox", { name: "Comment", exact: true });
  await expect(comment).toBeFocused();
  await page.keyboard.type(question);
  await page.keyboard.press("Enter");
}

const askAboutFirstLine = (page: Page, question: string) => askAboutLine(page, 1, question);

test("a comment sent from an editor selection is answered under that selection", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "inline-reply");
  await openFile(page, "notes.txt");
  await askAboutFirstLine(page, "why this line");

  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".inline-reply-question")).toHaveText("why this line");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");
  await expect(card.locator(".agent-working")).toHaveCount(0);
  // The card sits between the line it was sent from and the next, pushing the code below it down.
  const lineTops = await page
    .locator(".monaco-editor .view-lines .view-line")
    .evaluateAll((lines) =>
      lines.map((line) => line.getBoundingClientRect().top).sort((a, b) => a - b),
    );
  const box = (await card.boundingBox())!;
  expect(box.y).toBeGreaterThan(lineTops[0]!);
  expect(lineTops[1]).toBeGreaterThanOrEqual(box.y + box.height);

  await card.getByRole("button", { name: "Dismiss", exact: true }).click();
  await expect(card).toHaveCount(0);
  await expect(
    surface.locator(".agent-markdown", { hasText: "echo: why this line" }),
  ).toBeVisible();

  await askAboutFirstLine(page, "and again");
  await askAboutFirstLine(page, "and once more");
  await expect(card).toHaveCount(2);
  await expect(card.last().locator(".agent-markdown")).toContainText("echo: and once more");
  await runCommand(page, "Dismiss Inline Replies");
  await expect(card).toHaveCount(0);
});

test("an inline reply follows a comment steered into a running turn", async ({ page, weavie }) => {
  const surface = await createAcpSession(page, "inline-reply-steer");
  const workspace = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(workspace, "hold-steer-reply"), "");
  await writeFile(join(workspace, "hold-steer-finish"), "");
  await submitAcpDraft(surface, "steer-visibility-message");
  await expect(surface).toContainText("Inspecting the build before your question.");
  await openFile(page, "notes.txt");
  await askAboutFirstLine(page, "what is building");

  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-working")).toContainText("Working");
  await expect(card.locator(".agent-markdown")).toHaveCount(0);
  await writeFile(join(workspace, "release-steer-reply"), "");
  await expect(card.locator(".agent-markdown")).toContainText(
    /running the build directly in this session/,
  );
  await expect(card.locator(".agent-working")).toBeVisible();
  await writeFile(join(workspace, "release-steer-finish"), "");
  await expect(card.locator(".agent-markdown").last()).toContainText("Build verified.");
  await expect(card.locator(".agent-working")).toHaveCount(0);
});

test("a comment sent from a line low in the view makes room for its reply", async ({ page }) => {
  await createAcpSession(page, "inline-reply-room");
  await openFile(page, "long.ts");
  // Line 30 of 160 sits near the bottom of the editor, where a card below it would be cut off.
  await askAboutLine(page, 30, "why this line");

  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");
  await expect(card).toBeInViewport({ ratio: 1 });
  await expect(
    page.locator(".monaco-editor .view-line", { hasText: "// line 30" }),
  ).toBeInViewport();
});
