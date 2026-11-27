import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, setFakeAcpMode, submitAcpDraft } from "../harness/acp-session";
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
  await expect(card.locator(".agent-aside-toggle-label")).toHaveText("why this line");
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

  // A long reply scrolls inside the card, which never outgrows the editor.
  const editorHeight = (await page.locator(".monaco-editor").first().boundingBox())!.height;
  expect(
    await card
      .locator(".inline-reply-body")
      .evaluate((element) => Number.parseFloat(getComputedStyle(element).maxHeight)),
  ).toBeCloseTo(editorHeight * 0.6, 0);

  await card.getByRole("button", { name: "Dismiss Inline Reply", exact: true }).click();
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
  // The card grows below the selection, which has not moved, so its toolbar stays for a second question.
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+Home");
  await page.keyboard.press("Shift+End");
  const comment = page.getByRole("button", { name: "Comment on Selection", exact: true });
  await expect(comment).toBeVisible();
  await writeFile(join(workspace, "release-steer-reply"), "");
  await expect(card.locator(".agent-markdown")).toContainText(
    /running the build directly in this session/,
  );
  await expect(card.locator(".agent-working")).toBeVisible();
  await expect(comment).toBeVisible();
  // And a comment being typed survives the rest of the reply.
  await comment.click();
  const draft = page.getByRole("textbox", { name: "Comment", exact: true });
  await draft.fill("still typing");
  await writeFile(join(workspace, "release-steer-finish"), "");
  await expect(card.locator(".agent-markdown").last()).toContainText("Build verified.");
  await expect(card.locator(".agent-working")).toHaveCount(0);
  await expect(draft).toHaveValue("still typing");
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

test("a queued comment's card ends when a restart drops the comment", async ({ page, weavie }) => {
  await setFakeAcpMode(page, weavie.home, "no-steering");
  const surface = await createAcpSession(page, "inline-reply-dropped");
  await submitAcpDraft(surface, "hold");
  await expect(surface).toContainText("Waiting for steering");
  await openFile(page, "notes.txt");
  await askAboutFirstLine(page, "why this line");

  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-aside-head small")).toHaveText("Queued");
  await runCommand(page, "Restart Agent");
  await expect(card).toHaveCount(0);
});

test("a card stays under its line through edits at that line", async ({ page }) => {
  await createAcpSession(page, "inline-reply-edits");
  await openFile(page, "hello.ts");
  await askAboutFirstLine(page, "why this line");
  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");
  const underFirstLine = async (): Promise<void> => {
    await expect(page.locator(".monaco-editor .view-line").first()).toContainText(
      "export function",
    );
    await expect
      .poll(async () => {
        const tops = await page
          .locator(".monaco-editor .view-lines .view-line")
          .evaluateAll((lines) =>
            lines.map((line) => line.getBoundingClientRect().top).sort((a, b) => a - b),
          );
        const box = (await card.boundingBox())!;
        return tops[0]! < box.y && box.y + box.height <= tops[1]!;
      })
      .toBe(true);
  };
  await underFirstLine();

  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+Home");
  await page.keyboard.press("End");
  await page.keyboard.press("Enter");
  await page.keyboard.type("// added below");
  await underFirstLine();

  await page.keyboard.press("ControlOrMeta+Home");
  await page.keyboard.press("ControlOrMeta+Shift+k");
  await expect(page.locator(".monaco-editor .view-line").first()).toContainText("// added below");
  await page.keyboard.press("ControlOrMeta+z");
  await underFirstLine();
});

test("with reduced motion a card folds and opens without easing", async ({ page }) => {
  await page.emulateMedia({ reducedMotion: "reduce" });
  await createAcpSession(page, "inline-reply-still");
  await openFile(page, "notes.txt");
  await askAboutFirstLine(page, "why this line");
  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");
  const head = card.locator(".agent-aside-toggle");
  const easing = () =>
    card.evaluate((root) =>
      [...root.querySelectorAll(".agent-card-content")].map(
        (box) => getComputedStyle(box).transitionDuration,
      ),
    );

  await head.click();
  await expect(head).toHaveAttribute("aria-expanded", "false");
  expect(new Set(await easing())).toEqual(new Set(["0s"]));
  await head.click();
  await expect(head).toHaveAttribute("aria-expanded", "true");
  expect(new Set(await easing())).toEqual(new Set(["0s"]));
});

test("a card folds to its question, takes a follow-up, and answers to its commands", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "inline-reply-thread");
  await openFile(page, "notes.txt");
  await askAboutFirstLine(page, "why this line");
  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");

  const head = card.locator(".agent-aside-toggle");
  await head.click();
  await expect(head).toHaveAttribute("aria-expanded", "false");
  await expect(card.locator(".agent-markdown")).toBeHidden();
  await head.click();
  await expect(card.locator(".agent-markdown")).toBeVisible();

  // The follow-up is about the same selection, and its answer joins the card even after other talk in the pane.
  await submitAcpDraft(surface, "something else");
  await expect(
    surface.locator(".agent-markdown", { hasText: "echo: something else" }),
  ).toBeVisible();
  await expect(card).not.toContainText("something else");
  await card.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = card.getByRole("textbox", { name: "Reply", exact: true });
  await expect(reply).toBeFocused();
  await expect(card.locator(".agent-attachment-selection")).toContainText("notes.txt:1:1-1:16");
  // The attached code alone is not a follow-up: its card finds it by its text.
  const send = card.locator(".agent-aside-reply-actions").getByRole("button", { name: "Reply" });
  await expect(send).toBeDisabled();
  await reply.fill("and the next one");
  await expect(send).toBeEnabled();
  await page.keyboard.press("Enter");
  await expect(card.locator(".agent-tone-user")).toContainText("and the next one");
  await expect(card.locator(".agent-markdown").last()).toContainText("echo: and the next one");
  await expect(card.locator(".agent-markdown").last()).toContainText("notes.txt:1:1-1:16");
  await expect(reply).toHaveCount(0);
  await expect(
    surface.locator(".agent-markdown", { hasText: "echo: and the next one" }),
  ).toBeVisible();

  // The card commands act on the card in view nearest the cursor.
  await clickIntoEditor(page);
  await runCommand(page, "Toggle Inline Reply");
  await expect(head).toHaveAttribute("aria-expanded", "false");
  // Once the fold has finished, opening the card must still hand its box the focus.
  await expect
    .poll(() =>
      card.evaluate(
        (root) =>
          [...root.querySelectorAll(".agent-card-content")].flatMap((box) => box.getAnimations())
            .length,
      ),
    )
    .toBe(0);
  await runCommand(page, "Reply to Inline Reply");
  await expect(head).toHaveAttribute("aria-expanded", "true");
  await expect(reply).toBeFocused();
  await page.keyboard.press("Escape");
  await expect(reply).toHaveCount(0);
  await clickIntoEditor(page);
  await runCommand(page, "Dismiss Inline Reply");
  await expect(card).toHaveCount(0);
});

test("review chords pressed in a card's reply box leave the pending change alone", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "inline-reply-review");
  const file = join(sessionWorktrees(weavie.workspace)[0]!, "notes.txt");
  await submitAcpDraft(surface, `fs:${file}`);
  await expect(surface).toContainText("fs: written through ACP");
  await openFile(page, "notes.txt");
  const pending = page.locator(".weavie-inline-added");
  await expect(pending).toHaveCount(1);
  await askAboutFirstLine(page, "why this line");
  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");

  await card.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = card.getByRole("textbox", { name: "Reply", exact: true });
  await expect(reply).toBeFocused();
  await page.keyboard.type("keep typing");
  await page.keyboard.press("ControlOrMeta+Backspace");
  await page.keyboard.press("ControlOrMeta+Enter");
  await expect(reply).not.toHaveValue("keep typing");
  await expect(pending).toHaveCount(1);
  await expect(page.locator(".weavie-inline-accepted")).toHaveCount(0);
  expect(await readFile(file, "utf8")).toBe("written through ACP");
});

test("a reply to a comment sent from the review is there after leaving its tab and returning", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "inline-reply-review-tab");
  const file = join(sessionWorktrees(weavie.workspace)[0]!, "notes.txt");
  await submitAcpDraft(surface, `fs:${file}`);
  await expect(surface).toContainText("fs: written through ACP");
  await runCommand(page, "Review Changes");
  await page
    .locator(".unified-review-file .view-line", { hasText: "written" })
    .click({ clickCount: 3 });
  await page.getByRole("button", { name: "Comment on Selection", exact: true }).click();
  await page.keyboard.type("why this line");
  await page.keyboard.press("Enter");
  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");

  await openFile(page, "hello.ts");
  await expect(card).toHaveCount(0);
  await page.locator(".editor-tab", { hasText: "Review" }).click();
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");
});

test("a reply is kept across a reload, folded as it was and under its code wherever it moved, until dismissed", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "inline-reply-reload");
  await openFile(page, "notes.txt");
  await askAboutFirstLine(page, "why this line");
  const card = page.locator("[data-inline-reply]");
  await expect(card.locator(".agent-markdown")).toContainText("echo: why this line");
  await card.getByRole("button", { name: "Reply", exact: true }).click();
  await card.getByRole("textbox", { name: "Reply", exact: true }).fill("and the next one");
  await page.keyboard.press("Enter");
  await expect(card.locator(".agent-markdown").last()).toContainText("echo: and the next one");
  const head = card.locator(".agent-aside-toggle");
  await head.click();
  await expect(head).toHaveAttribute("aria-expanded", "false");
  await expect(
    surface.locator(".agent-markdown", { hasText: "echo: and the next one" }),
  ).toBeVisible();

  // The file changes while the page is gone: its line moves down one.
  const file = join(sessionWorktrees(weavie.workspace)[0]!, "notes.txt");
  await writeFile(file, "a line above\njust plain text\n");
  await page.reload();
  await openFile(page, "notes.txt");
  await expect(head).toHaveAttribute("aria-expanded", "false");
  const line = page.locator(".monaco-editor .view-line", { hasText: "just plain text" });
  const below = page.locator(".monaco-editor .view-line", { hasText: "a line above" });
  await expect(line).toBeVisible();
  expect((await card.boundingBox())!.y).toBeGreaterThanOrEqual(
    (await line.boundingBox())!.y + (await line.boundingBox())!.height - 1,
  );
  expect((await below.boundingBox())!.y).toBeLessThan((await card.boundingBox())!.y);
  await head.click();
  await expect(card.locator(".agent-markdown").last()).toContainText("echo: and the next one");

  await card.getByRole("button", { name: "Dismiss Inline Reply", exact: true }).click();
  await expect(card).toHaveCount(0);
  await page.reload();
  await openFile(page, "notes.txt");
  await expect(
    page.locator(".monaco-editor .view-line", { hasText: "just plain text" }),
  ).toBeVisible();
  await expect(page.locator('[data-surface="structured-agent"]')).toContainText(
    "echo: why this line",
  );
  await expect(card).toHaveCount(0);
});

test.describe("with permissions asked", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: false } },
      ],
    },
  });

  test("a request the agent makes is answered from the card, with the agent pane hidden", async ({
    page,
    weavie,
  }) => {
    await createAcpSession(page, "inline-reply-approval");
    await openFile(page, "notes.txt");
    await clickIntoEditor(page);
    await runCommand(page, "Toggle Fullscreen Pane");
    await writeFile(join(sessionWorktrees(weavie.workspace)[0]!, "next-instruction"), "permission");
    await askAboutFirstLine(page, "run the tests for this");

    const card = page.locator("[data-inline-reply]");
    await expect(card.locator(".agent-working")).toContainText("Waiting on your approval");
    // A collapsed card still shows what it is waiting on.
    await card.locator(".agent-aside-toggle").click();
    await card.getByRole("button", { name: "Allow", exact: true }).click();
    await card.locator(".agent-aside-toggle").click();
    await expect(card.locator(".agent-markdown")).toContainText("permission: allow-once");
    await expect(card.locator(".agent-working")).toHaveCount(0);
  });
});
