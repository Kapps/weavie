import { rm, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { deferredPhase, describeSample, frames, measureRender } from "../harness/render-budget";

// Scrolling a twelve-block walkthrough top to bottom: layout is read to mount blocks and find the current stop.
const NOTCHES = 60;
const READS_PER_NOTCH = 1;

const claims = [
  "export class Claims {",
  "  private staged = new Map<string, string>();",
  "  private find = (id: string) => id;",
  "  private bound = false;",
  "  stage(operation: string): string {",
  "    const token = `weavie-block:${Math.random()}`;",
  "    this.staged.set(token, operation);",
  "    return token;",
  "  }",
  "}",
  "",
].join("\n");

const published = {
  id: "claims",
  title: "How a chart lands on its own row",
  problem: "A tool call and its row arrive apart.\n\nSo how does Weavie know a row is the chart's?",
  gist: "The tool holds the chart under a random ticket and hands the ticket back.",
  example: 'showBlock { id: "p95" }',
  stops: [
    {
      id: "stage",
      name: "Waiting room",
      title: "The waiting room",
      lead: "A dictionary from ticket to the held chart.",
      code: [
        {
          path: "claims.ts",
          from: 1,
          to: 10,
          folds: [{ from: 3, to: 4, label: "lookups the conversation supplies" }],
          notes: [
            { line: 6, token: "Math.random()", text: "Nothing else in a result looks like it" },
            { line: 10, token: "}", text: "The class ends here" },
          ],
        },
      ],
      state: {
        cells: [
          { label: "Waiting for a row", value: "p95" },
          { label: "Saved", value: "" },
        ],
      },
      predict: {
        question: "Do two parallel charts collide?",
        answer: "No: each has its own ticket.",
      },
    },
    {
      id: "take",
      name: "Take",
      title: "A row comes back",
      lead: "The row that carries the ticket takes the chart.",
      code: [{ path: "claims.ts", from: 5, to: 9 }],
    },
  ],
};

async function publish(page: Page, weavie: { workspace: string }, branch: string) {
  const surface = await createAcpSession(page, branch);
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(worktree, "claims.ts"), claims);
  await submitAcpDraft(surface, `block:claude:publishWalkthrough:${JSON.stringify(published)}`);
  const card = surface.locator('[data-agent-walkthrough="claims"]');
  await expect(card.locator(".agent-aside-head")).toContainText(
    "Walkthrough · How a chart lands on its own row",
  );
  return { surface, card, worktree };
}

const tab = (page: Page): Locator => page.locator(".walkthrough-page");

// Monaco's text layer covers view zones, so a band is clicked at its point, the way a user does.
async function clickPoint(page: Page, target: Locator): Promise<void> {
  await expect(target).toBeInViewport();
  const box = (await target.boundingBox())!;
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
}
// Monaco splits a marked token across its highlighting spans.
const marked = async (block: Locator): Promise<string> =>
  (await block.locator(".walkthrough-mark").allTextContents()).join("");

test("a published walkthrough reads as stops through real code, with its stops along the bottom", async ({
  page,
  weavie,
}) => {
  const { card } = await publish(page, weavie, "walkthrough-read");
  await expect(card.locator(".walkthrough-stop-row")).toHaveText([
    /1The waiting room/,
    /2A row comes back/,
  ]);

  await card.locator(".walkthrough-stop-row").nth(1).click();
  const page_ = tab(page);
  // The tab opens at the stop picked, and the strip follows the reader.
  await expect(page_.locator(".walkthrough-strip-stop").nth(1)).toHaveClass(/is-current/);
  await expect(page_.locator(".walkthrough-title")).toHaveText("How a chart lands on its own row");
  await expect(page.locator(".editor-tab.active")).toContainText(
    "How a chart lands on its own row",
  );
  await expect(page_.locator(".walkthrough-strip-stop")).toHaveText([
    /1\s*Waiting room/,
    /2\s*Take/,
  ]);

  const block = page_.locator(".walkthrough-code").first();
  await expect(block.locator(".walkthrough-file")).toContainText("claims.ts");
  await expect(block.locator(".walkthrough-note")).toHaveText([
    "Nothing else in a result looks like it",
    "The class ends here",
  ]);
  await expect.poll(() => marked(block)).toBe("Math.random()}");
  // A note under the block's last line is drawn, though the line after it is hidden.
  await block.scrollIntoViewIfNeeded();
  await expect(
    block.locator(".walkthrough-note", { hasText: "The class ends here" }),
  ).toBeInViewport();
  await expect(block.locator(".view-lines")).not.toContainText("private bound");
  await clickPoint(
    page,
    block.locator(".code-band", { hasText: "2 lines · lookups the conversation supplies" }),
  );
  await expect(block.locator(".view-lines")).toContainText("private bound");

  await expect(page_.locator(".walkthrough-state")).toContainText("Waiting for a row");
  await expect(page_.locator(".walkthrough-state")).toContainText("nothing");
  await page_.getByRole("button", { name: "Show the Answer" }).click();
  await expect(page_.locator(".walkthrough-answer")).toHaveText("No: each has its own ticket.");

  await page_.locator(".walkthrough-strip-stop").first().click();
  await expect(page_.locator(".walkthrough-strip-stop").first()).toHaveClass(/is-current/);
});

test("an update marks the stops the change altered and paints the diff", async ({
  page,
  weavie,
}) => {
  const { surface, card, worktree } = await publish(page, weavie, "walkthrough-update");
  await writeFile(
    join(worktree, "claims.ts"),
    claims.replace("Math.random()", "crypto.randomUUID()"),
  );
  const update = {
    id: "claims",
    change: "Tickets come from randomUUID.",
    stops: [
      {
        id: "stage",
        effect: "changed",
        note: "Was: Math.random. Now: a UUID.",
        code: [
          {
            path: "claims.ts",
            from: 1,
            to: 10,
            notes: [{ line: 6, token: "crypto.randomUUID()", text: "A UUID" }],
          },
        ],
      },
      { id: "take", effect: "affected", note: "The ticket is longer now." },
    ],
  };
  await submitAcpDraft(surface, `block:codex:updateWalkthrough:${JSON.stringify(update)}`);
  await expect(card.locator(".walkthrough-tag")).toHaveText(["changed", "plays out differently"]);

  await card.getByRole("button", { name: "Open", exact: true }).click();
  const page_ = tab(page);
  await expect(page_.locator(".walkthrough-update")).toContainText("Tickets come from randomUUID.");
  await expect(page_.locator(".walkthrough-strip .walkthrough-dot")).toHaveCount(2);
  const block = page_.locator(".walkthrough-code").first();
  await expect.poll(() => marked(block)).toBe("crypto.randomUUID()");
  await expect(block.locator(".weavie-inline-removed")).toContainText("Math.random()");
});

test("selecting text in a walkthrough asks a side question answered under it", async ({
  page,
  weavie,
}) => {
  const { surface, card } = await publish(page, weavie, "walkthrough-ask");
  await card.getByRole("button", { name: "Open", exact: true }).click();
  const page_ = tab(page);
  const gist = page_.locator('[data-place="gist"]');
  await expect(gist).toContainText("random ticket");

  await gist.evaluate((element) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node !== null && !node.textContent!.includes("random ticket")) node = walker.nextNode();
    const start = node!.textContent!.indexOf("random ticket");
    const range = document.createRange();
    range.setStart(node!, start);
    range.setEnd(node!, start + "random ticket".length);
    getSelection()!.removeAllRanges();
    getSelection()!.addRange(range);
    element.dispatchEvent(new PointerEvent("pointerup", { bubbles: true }));
  });
  await page.getByRole("button", { name: "Comment on Selection" }).click();
  await page.getByRole("textbox", { name: "Comment" }).fill("Why random?");
  await page.getByRole("button", { name: "Send", exact: true }).click();

  const question = page_.locator("[data-walkthrough-question]");
  await expect(question.locator(".agent-aside-head")).toContainText("Why random?");
  await expect(question.locator(".walkthrough-quote")).toHaveText("random ticket");
  await expect(question).toContainText("echo: Why random?");
  await expect(question.getByRole("button", { name: "Reply" })).toBeVisible();
  await expect(surface.locator(".agent-entry-aside, [data-agent-aside]")).toHaveCount(0);

  await question.getByRole("button", { name: "Collapse Why random?" }).click();
  await expect(question.locator(".walkthrough-quote")).toBeHidden();
  await expect(question.locator(".agent-aside-head")).toContainText("Why random?");
});

test("a question about code sits under the line it was asked about", async ({ page, weavie }) => {
  const { card } = await publish(page, weavie, "walkthrough-ask-code");
  await card.getByRole("button", { name: "Open", exact: true }).click();
  const block = tab(page).locator(".walkthrough-code").first();
  await expect.poll(() => marked(block)).toBe("Math.random()}");

  // Two lines: the card, its quote and its mark go with the last.
  await block.locator(".view-line", { hasText: "const token" }).click();
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+ArrowDown");
  await page.keyboard.press("Shift+End");
  await page.getByRole("button", { name: "Comment on Selection" }).click();
  await page.getByRole("textbox", { name: "Comment" }).fill("Why a map?");
  await page.getByRole("button", { name: "Send", exact: true }).click();

  const question = block.locator("[data-walkthrough-question]");
  await expect(question.locator(".agent-aside-head")).toContainText("Why a map?");
  await expect(question.locator(".walkthrough-quote")).toHaveText(
    "this.staged.set(token, operation);",
  );
  await expect(question).toContainText("echo: Why a map?");
  await expect
    .poll(async () =>
      // Monaco draws spaces as non-breaking ones.
      (await block.locator(".walkthrough-asked").allTextContents())
        .join("")
        .replace(/\u00a0/g, " "),
    )
    .toBe("this.staged.set(token, operation);");
  // Under line 7, the one selected, and above line 8.
  const top = async (locator: Locator) => (await locator.boundingBox())!.y;
  const asked = block.locator(".view-line", { hasText: "this.staged.set" });
  const next = block.locator(".view-line", { hasText: "return token" });
  expect(await top(question)).toBeGreaterThan(await top(asked));
  expect(await top(question)).toBeLessThan(await top(next));
});

test("a BTW card's Stop ends its turn and leaves the main conversation alone", async ({ page }) => {
  const surface = await createAcpSession(page, "walkthrough-stop-btw");
  await submitAcpDraft(surface, "/btw hold");
  const aside = surface.locator("[data-agent-aside]");
  const stop = aside.getByRole("button", { name: "Stop", exact: true });
  await expect(stop).toBeVisible();
  // Playwright clicks a transparent button, so its opacity is what proves a reader can see it.
  await expect(stop).toHaveCSS("opacity", "1");
  await stop.click();
  await expect(stop).toHaveCount(0);
  await expect(aside.getByRole("button", { name: "Reply" })).toBeVisible();
});

test("a deleted file shows the code the walkthrough recorded", async ({ page, weavie }) => {
  const { card, worktree } = await publish(page, weavie, "walkthrough-deleted");
  await rm(join(worktree, "claims.ts"));
  await card.getByRole("button", { name: "Open", exact: true }).click();
  const blocks = tab(page).locator(".walkthrough-code");
  await expect(blocks.first().locator(".walkthrough-notice")).toHaveText(
    "Deleted since this walkthrough recorded it",
  );
  await expect(blocks.first().locator(".view-lines")).toContainText("export class Claims");
  await blocks.nth(1).scrollIntoViewIfNeeded();
  await expect(blocks.nth(1).locator(".view-lines")).toContainText("stage(operation");
  await expect(blocks.nth(1).locator(".walkthrough-notice")).toHaveText(
    "Deleted since this walkthrough recorded it",
  );
});

test("the review bar stays with its file, not over a walkthrough tab", async ({ page, weavie }) => {
  const { surface, card, worktree } = await publish(page, weavie, "walkthrough-review-bar");
  await submitAcpDraft(surface, `fs:${join(worktree, "notes.txt")}`);
  await expect(surface).toContainText("fs: written through ACP");
  await openFile(page, "notes.txt");
  const bar = page.locator(".weavie-inline-toolbar");
  await expect(bar).toBeVisible();

  await card.getByRole("button", { name: "Open", exact: true }).click();
  await expect(tab(page)).toBeVisible();
  await expect(bar).toHaveCount(0);
});

test.describe("render budget", () => {
  test.use({ preNavigate: deferredPhase });

  test("scrolling a walkthrough of twelve blocks rebuilds nothing it already drew @perf", async ({
    page,
    weavie,
  }) => {
    const surface = await createAcpSession(page, "walkthrough-budget");
    const worktree = sessionWorktrees(weavie.workspace)[0]!;
    await writeFile(join(worktree, "claims.ts"), claims);
    const stops = Array.from({ length: 12 }, (_, index) => ({
      id: `s${index}`,
      name: `Stop ${index}`,
      title: `Stop ${index}`,
      lead: "Read it.",
      code: [
        {
          path: "claims.ts",
          from: 1,
          to: 10,
          notes: [{ line: 6, token: "Math.random()", text: "A note" }],
        },
      ],
    }));
    await submitAcpDraft(
      surface,
      `block:claude:publishWalkthrough:${JSON.stringify({ ...published, stops })}`,
    );
    await surface
      .locator('[data-agent-walkthrough="claims"]')
      .getByRole("button", { name: "Open", exact: true })
      .click();
    const page_ = tab(page);
    await expect(page_.locator(".walkthrough-note").first()).toBeVisible();
    await page_.hover();
    const sample = await measureRender(page, NOTCHES, NOTCHES * READS_PER_NOTCH, async () => {
      await page.mouse.wheel(0, 120);
      await frames(page, 1);
    });
    const report = describeSample(sample);
    expect(sample.removed, report).toBe(0);
    expect(sample.reads, report).toBeLessThanOrEqual(NOTCHES * READS_PER_NOTCH);
    await expect(page_.locator(".walkthrough-strip-stop").last()).toHaveClass(/is-current/);
    // A jump back to the top, as Home or a scrollbar drag makes, skips every stop's heading on the way.
    await page_.evaluate((element) => {
      element.closest(".editor-agent-view")!.scrollTop = 0;
    });
    await expect(page_.locator(".walkthrough-strip-stop").first()).toHaveClass(/is-current/);
    await expect(page_.getByRole("button", { name: "Previous Stop" })).toBeDisabled();
  });
});

test("an empty line still has a fold's row under it and can end a question's selection", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "walkthrough-empty-fold");
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(
    join(worktree, "fields.ts"),
    "class Fields {\n\n  a = 1;\n  b = 2;\n  c = 3;\n}\n",
  );
  const walkthrough = {
    ...published,
    stops: [
      {
        id: "fields",
        name: "Fields",
        title: "Fields",
        lead: "Read it.",
        code: [
          {
            path: "fields.ts",
            from: 1,
            to: 6,
            folds: [{ from: 3, to: 4, label: "two fields" }],
          },
        ],
      },
    ],
  };
  await submitAcpDraft(surface, `block:claude:publishWalkthrough:${JSON.stringify(walkthrough)}`);
  await surface
    .locator('[data-agent-walkthrough="claims"]')
    .getByRole("button", { name: "Open", exact: true })
    .click();
  const block = tab(page).locator(".walkthrough-code").first();
  const fold = block.locator(".code-band", { hasText: "2 lines · two fields" });
  await expect(fold).toBeInViewport();
  await expect(block.locator(".view-lines")).not.toContainText("a = 1");
  await clickPoint(page, fold);
  await expect(block.locator(".view-lines")).toContainText("a = 1");

  // A selection running onto the empty line is asked about from the last line with text.
  await block.locator(".view-line", { hasText: "class Fields" }).click();
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+ArrowDown");
  await page.keyboard.press("Shift+ArrowDown");
  await page.getByRole("button", { name: "Comment on Selection" }).click();
  await page.getByRole("textbox", { name: "Comment" }).fill("Why a class?");
  await page.getByRole("button", { name: "Send", exact: true }).click();
  await expect(block.locator("[data-walkthrough-question] .walkthrough-quote")).toHaveText(
    "class Fields {",
  );
});
