import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { clickIntoEditor, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("reply selections require an action and retain independent comments and excerpts", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "attach-selection");
  await submitAcpDraft(surface, "alpha bravo charlie");
  await submitAcpDraft(surface, "delta echo foxtrot");
  const first = surface.locator(".agent-markdown", { hasText: "echo: alpha bravo charlie" });
  const second = surface.locator(".agent-markdown", { hasText: "echo: delta echo foxtrot" });
  await expect(second).toBeVisible();
  const chips = surface.locator("[data-agent-composer] .agent-attachment-selection");

  await highlight(page, first);
  await expect(page.getByRole("toolbar", { name: "Selection actions" })).toBeVisible();
  await expect(chips).toHaveCount(0);
  const iconColors = await page
    .getByRole("toolbar", { name: "Selection actions" })
    .evaluate((toolbar) => {
      const foreground = getComputedStyle(document.querySelector(".app")!).color;
      return [...toolbar.querySelectorAll("svg")].map((icon) => ({
        foreground,
        stroke: getComputedStyle(icon).stroke,
        width: icon.getBoundingClientRect().width,
      }));
    });
  expect(iconColors).toHaveLength(2);
  for (const icon of iconColors) {
    expect(icon.stroke).toBe(icon.foreground);
    expect(icon.width).toBeGreaterThan(0);
  }
  await page.getByRole("button", { name: "Attach Selection to Prompt", exact: true }).click();
  await expect(chips).toHaveCount(1);

  await highlight(page, second);
  await page.getByRole("button", { name: "Comment on Selection", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Comment on Selection" });
  await expect(page.locator(".modal-backdrop")).toHaveCount(0);
  await expect(dialog.getByRole("button", { name: "Add to context", exact: true })).toBeDisabled();
  await dialog.getByRole("textbox", { name: "Comment", exact: true }).fill("discard this note");
  await dialog.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(chips).toHaveCount(1);

  await highlight(page, second);
  await page.getByRole("button", { name: "Comment on Selection", exact: true }).click();
  await dialog
    .getByRole("textbox", { name: "Comment", exact: true })
    .fill("Explain the second phrase");
  await dialog.getByRole("button", { name: "Add to context", exact: true }).click();
  await expect(chips).toHaveCount(2);
  await expect(chips.nth(0)).toContainText("echo: alpha bravo charlie");
  await expect(chips.nth(1)).toContainText("Explain the second phrase");

  await highlight(page, first);
  await page.getByRole("button", { name: "Attach Selection to Prompt", exact: true }).click();
  await expect(chips).toHaveCount(3);
  await chips.nth(2).getByRole("button", { name: "Remove attachment" }).click();
  await expect(chips).toHaveCount(2);
  await expect(chips.nth(1)).toContainText("Explain the second phrase");
  await submitAcpDraft(surface, "answer both");
  await expect(chips).toHaveCount(0);
  const answer = surface.locator(".agent-markdown", { hasText: "echo: answer both" });
  await expect(answer).toContainText("Attached selection (echo: alpha bravo charlie):");
  await expect(answer).toContainText(
    "echo: delta echo foxtrot\n\nComment:\nExplain the second phrase",
  );
  await expect(answer).not.toContainText("discard this note");
});

test("the palette retains a reply selection until the user moves elsewhere", async ({ page }) => {
  const surface = await createAcpSession(page, "attach-selection-command");
  await submitAcpDraft(surface, "delta echo foxtrot");
  const reply = surface.locator(".agent-markdown", { hasText: "echo: delta echo foxtrot" });
  await expect(reply).toBeVisible();
  await highlight(page, reply);
  await page.keyboard.press("ControlOrMeta+Shift+p");
  await page.locator(".tb-omnibar-input").fill(">Attach Selection to Prompt");
  await page
    .locator(".tb-omnibar-row")
    .filter({
      has: page.locator(".tb-row-leaf", { hasText: /^Attach Selection to Prompt$/ }),
    })
    .click();
  const chips = surface.locator("[data-agent-composer] .agent-attachment-selection");
  await expect(chips).toHaveCount(1);
  await expect(chips).toContainText("echo: delta echo foxtrot");

  await highlight(page, reply);
  await surface.locator("[data-agent-composer] textarea").click();
  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill(">Attach Selection to Prompt");
  await expect(
    page.locator(".tb-omnibar-row").filter({
      has: page.locator(".tb-row-leaf", { hasText: /^Attach Selection to Prompt$/ }),
    }),
  ).toHaveCount(0);
});

test("editor selections retain automatic context and can pin excerpts with source comments", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "editor-selection-context");
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+a");
  const chips = surface.locator("[data-agent-composer] .agent-attachment-selection");
  await expect(page.getByRole("toolbar", { name: "Selection actions" })).toBeVisible();
  await expect(chips).toHaveCount(0);
  await submitAcpDraft(surface, "automatic context");
  await expect(
    surface.locator(".agent-markdown", { hasText: "echo: automatic context" }),
  ).toBeVisible();
  const transcript = join(weavie.home, ".weavie", "fake-acp-state", "wire-prompts.jsonl");
  const prompts = (await readFile(transcript, "utf8"))
    .trim()
    .split("\n")
    .map((line) => JSON.parse(line));
  const context = prompts
    .at(-1)
    .parameters.prompt.find((block: { resource?: { uri: string } }) =>
      block.resource?.uri.includes("#selection"),
    );
  expect(context.resource.text).toContain("just plain text");

  await clickIntoEditor(page);
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await page.getByRole("button", { name: "Attach Selection to Prompt", exact: true }).click();
  await expect(chips).toHaveCount(1);
  await expect(chips).toContainText("notes.txt:1:1-1:16");
  await clickIntoEditor(page);
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await page.getByRole("button", { name: "Comment on Selection", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Comment on Selection" });
  await dialog
    .getByRole("textbox", { name: "Comment", exact: true })
    .fill("Rewrite this specification line");
  await dialog.getByRole("button", { name: "Add to context", exact: true }).click();
  await expect(chips).toHaveCount(2);
  await clickIntoEditor(page);
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await expect(page.getByRole("toolbar", { name: "Selection actions" })).toBeVisible();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("toolbar", { name: "Selection actions" })).toHaveCount(0);
  await expect(chips).toHaveCount(2);
  await submitAcpDraft(surface, "apply the note");
  const answer = surface.locator(".agent-markdown", { hasText: "echo: apply the note" });
  await expect(answer).toContainText("notes.txt:1:1-1:16");
  await expect(answer).toContainText(
    "just plain text\n\nComment:\nRewrite this specification line",
  );
  await expect(chips).toHaveCount(0);
});

async function highlight(page: Page, reply: Locator): Promise<void> {
  // Completed replies replace their paragraphs once, so measure after the turn settles.
  await expect(page.locator(".agent-working")).toHaveCount(0);
  await reply.scrollIntoViewIfNeeded();
  const box = (await reply.locator("p").first().boundingBox())!;
  await page.mouse.move(box.x + 1, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 1, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
}
