import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

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
// Monaco splits a marked token across its highlighting spans.
const marked = async (block: Locator): Promise<string> =>
  (await block.locator(".walkthrough-mark").allTextContents()).join("");

test("a published walkthrough reads as stops through real code, with its stops along the bottom", async ({
  page,
  weavie,
}) => {
  const { card } = await publish(page, weavie, "walkthrough-read");
  await expect(card.locator(".walkthrough-card-stop")).toHaveText([
    /1The waiting room/,
    /2A row comes back/,
  ]);

  await card.locator(".walkthrough-card-stop").nth(1).click();
  const page_ = tab(page);
  await expect(page_.locator(".walkthrough-title")).toHaveText("How a chart lands on its own row");
  await expect(page.locator(".editor-tab.active")).toContainText(
    "How a chart lands on its own row",
  );
  await expect(page_.locator(".walkthrough-strip-stop")).toHaveText([/1Waiting room/, /2Take/]);

  const block = page_.locator(".walkthrough-code").first();
  await expect(block.locator(".walkthrough-file")).toContainText("claims.ts");
  await expect(block.locator(".walkthrough-note")).toHaveText(
    "Nothing else in a result looks like it",
  );
  await expect.poll(() => marked(block)).toBe("Math.random()");
  await expect(block.locator(".view-lines")).not.toContainText("private bound");
  await block.getByRole("button", { name: /2 lines · lookups the conversation supplies/ }).click();
  await expect(block.locator(".view-lines")).toContainText("private bound");

  await expect(page_.locator(".walkthrough-state")).toContainText("Waiting for a row");
  await expect(page_.locator(".walkthrough-state")).toContainText("nothing");
  await page_.getByRole("button", { name: "Show the Answer" }).click();
  await expect(page_.locator(".walkthrough-answer")).toHaveText("No: each has its own ticket.");

  await page_.locator(".walkthrough-strip-stop").nth(1).click();
  await expect(page_.locator(".walkthrough-strip-stop").nth(1)).toHaveClass(/is-current/);
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

  await question.getByRole("button", { name: "Dismiss" }).click();
  await expect(question).toHaveCount(0);
});
