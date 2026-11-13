import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import {
  awaitEditorReady,
  clickIntoEditor,
  createSession,
  openFile,
  runCommand,
} from "../harness/actions";
import type { FakeStep } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { openCommentedFile, prCard } from "../harness/pull-request";
import { openPullRequest, hintPopover as popover } from "../harness/review";

// Next/Previous Comment or Hint: one sequence of a session's PR threads and review hints, in the review's file order,
// with its position in the review header and on the current item. See docs/specs/review-points-of-interest/.

const hint: FakeStep = {
  op: "hook",
  request: {
    hook_event_name: "PostToolUse",
    tool_name: "ReportFindings",
    cwd: "{{WORKSPACE}}",
    tool_input: {
      findings: [
        {
          file: "hello.ts",
          line: 5,
          summary: "The PR greeting is hardcoded",
          verdict: "CONFIRMED",
        },
      ],
    },
  },
};

test.use({ prScenario: true, fakeScript: { steps: [hint] } });

const position = (page: Page): Locator =>
  page.locator(".unified-review-header .review-item-position > span");
const next = (page: Page) => page.keyboard.press("ControlOrMeta+Alt+BracketRight");
const lineNumber = (page: Page) =>
  page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()?.lineNumber);
const hintLine = (page: Page) =>
  page.evaluate(
    () =>
      window.__WEAVIE_EDITOR__
        ?.getModel()
        ?.getAllDecorations()
        .find((decoration) => decoration.options.className?.includes("weavie-hint-line"))?.range
        .startLineNumber,
  );

// Opens PR #101's unified review: feature.ts (an open and a resolved thread) then hello.ts (a thread and a hint).
async function openReview(page: Page): Promise<void> {
  await openPullRequest(page);
  await expect(page.locator(".weavie-hint-pill")).toHaveCount(1, { timeout: 15_000 });
  await expect(position(page)).toHaveText("3");
}

test("steps by key across the review's files, wrapping, with the position in the header and on the item", async ({
  page,
}) => {
  test.slow();
  await openReview(page);
  const resolved = prCard(page, "Name it after the feature");
  await expect(resolved.locator(".weavie-pr-badge-resolved")).toHaveText("Resolved");
  await expect(resolved.locator(".weavie-pr-summary")).toBeVisible();

  // From the overview, the first step lands on the first item; it focuses the code, so the key steps on.
  await runCommand(page, "Next Comment or Hint");
  const flag = prCard(page, "Should this flag default to off?");
  await expect(flag).toHaveClass(/weavie-pr-card-current/);
  await expect(position(page)).toHaveText("1 of 3");
  await next(page);
  const greeting = prCard(page, "Why change this greeting?");
  await expect(greeting).toHaveClass(/weavie-pr-card-current/);
  await expect(position(page)).toHaveText("2 of 3");
  await expect(greeting.locator(".review-item-position > span")).toHaveText("2 of 3");
  await expect(flag).not.toHaveClass(/weavie-pr-card-current/);

  await next(page);
  await expect(popover(page).locator(".weavie-hint-title")).toHaveText(
    "The PR greeting is hardcoded",
  );
  await expect(popover(page).locator(".review-item-position > span")).toHaveText("3 of 3");
  await expect(greeting).not.toHaveClass(/weavie-pr-card-current/);

  await next(page);
  await expect(flag).toHaveClass(/weavie-pr-card-current/);
  await expect(popover(page)).toHaveCount(0);
  await expect(position(page)).toHaveText("1 of 3");

  // A click in the code clears the outlined thread; the next step starts from where the click put the cursor, below
  // hello.ts's last item, so it wraps to the first.
  await next(page);
  await expect(position(page)).toHaveText("2 of 3");
  // At the line's start: its end carries the blame annotation, whose popover takes focus.
  await page
    .locator(".view-line", { hasText: "console.log(message)" })
    .click({ position: { x: 4, y: 4 } });
  await expect(greeting).not.toHaveClass(/weavie-pr-card-current/);
  await expect(position(page)).toHaveText("3");
  await next(page);
  await expect(position(page)).toHaveText("1 of 3");

  // Escape clears it too, and with the cursor still where the step left it, either step moves on from that item.
  await page.keyboard.press("Escape");
  await expect(position(page)).toHaveText("3");
  await next(page);
  await expect(position(page)).toHaveText("2 of 3");
  await page.keyboard.press("Escape");
  await page.keyboard.press("ControlOrMeta+Alt+BracketLeft");
  await expect(position(page)).toHaveText("1 of 3");
  await page.mouse.wheel(0, 400);
  await expect(position(page)).toHaveText("1 of 3");
});

test("a hovered hint's arrows step from that hint, and the header's from the current item", async ({
  page,
}) => {
  await openReview(page);
  await runCommand(page, "Next Comment or Hint");
  await next(page);
  const greeting = prCard(page, "Why change this greeting?");
  await expect(greeting).toHaveClass(/weavie-pr-card-current/);

  // The hint is item 3; its popover's Next wraps to item 1, not to item 3 after the current item 2.
  await page.locator(".weavie-hint-pill").hover();
  await expect(popover(page).locator(".review-item-position > span")).toHaveText("3 of 3");
  // The count reads on the line of the popover's own text.
  const baseline = (selector: string) =>
    popover(page)
      .locator(selector)
      .evaluate((element) => {
        const probe = document.createElement("span");
        probe.style.cssText = "display:inline-block;width:0;height:0";
        element.prepend(probe);
        const y = probe.getBoundingClientRect().bottom;
        probe.remove();
        return y;
      });
  expect(
    Math.abs((await baseline(".review-item-count")) - (await baseline(".weavie-hint-who"))),
  ).toBeLessThanOrEqual(0.5);
  await expect(position(page)).toHaveText("2 of 3");
  await popover(page).getByRole("button", { name: "Next Comment or Hint" }).click();
  await expect(position(page)).toHaveText("1 of 3");

  await page
    .locator(".unified-review-header")
    .getByRole("button", { name: "Next Comment or Hint" })
    .click();
  await expect(position(page)).toHaveText("2 of 3");
  await expect(greeting).toHaveClass(/weavie-pr-card-current/);
});

test("the filter hides a kind, adds resolved threads, and is remembered; Dismiss drops a hint", async ({
  page,
}) => {
  test.slow();
  await openReview(page);
  const filterButton = page.getByRole("button", { name: "Filter Comments and Hints" });
  const filter = async (row: string) => {
    await filterButton.click();
    await page.locator(".context-menu-item", { hasText: row }).click();
  };

  await filter("Comments");
  await expect(position(page)).toHaveText("1");
  await expect(page.locator(".weavie-pr-card")).toHaveCount(0);
  await filter("Comments");
  await expect(position(page)).toHaveText("3");

  await filter("Hints");
  await expect(position(page)).toHaveText("2");
  await expect(page.locator(".weavie-hint-pill")).toHaveCount(0);
  await filter("Resolved and outdated");
  await expect(position(page)).toHaveText("3");
  await filterButton.click();
  const rows = page.locator(".context-menu-item");
  await expect(rows.filter({ hasText: "Comments" }).locator(".context-menu-detail")).toHaveText(
    "2",
  );
  await expect(rows.filter({ hasText: "Hints" }).locator(".context-menu-check svg")).toHaveCount(0);
  await expect(rows.filter({ hasText: "Resolved" }).locator(".context-menu-check svg")).toHaveCount(
    1,
  );
  await page.keyboard.press("Escape");

  // The reload restores the review tab; the overview command opens it either way.
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await runCommand(page, "Show Session Overview");
  await expect(position(page)).toHaveText("3");
  await expect(page.locator(".weavie-hint-pill")).toHaveCount(0);

  await filter("Hints");
  await filter("Resolved and outdated");
  await expect(position(page)).toHaveText("3");
  await runCommand(page, "Previous Comment or Hint");
  await expect(popover(page).locator(".weavie-hint-title")).toHaveText(
    "The PR greeting is hardcoded",
  );
  await popover(page).getByRole("button", { name: "Dismiss hint" }).click();
  await expect(position(page)).toHaveText("2");
});

test.describe("without a pull request", () => {
  test.use({ prScenario: false, fakeScript: null });

  // A session with a hint on line 80 of long.ts, which the agent changed on line 5 only.
  async function hintedSession(page: Page, worktreeOf: () => string): Promise<void> {
    await awaitEditorReady(page);
    await createSession(page, { branch: "stepping", provider: "fake-acp" });
    const worktree = worktreeOf();
    const lines = Array.from({ length: 160 }, (_, i) => `// line ${i + 1}`);
    lines[4] = "// changed five";
    await writeFile(join(worktree, "long.ts"), `${lines.join("\n")}\n`);
    execFileSync("git", ["commit", "-qam", "change"], { cwd: worktree });
    const composer = page.locator(
      '[data-surface="structured-agent"] [data-agent-composer] textarea',
    );
    await composer.click();
    await composer.fill(
      `mcp:addReviewHint:${JSON.stringify({ path: "long.ts", line: 80, severity: "bug", text: "Folded hint" })}`,
    );
    await composer.press("Enter");
    await expect(page.locator('[data-surface="structured-agent"]')).toContainText(
      "Hint added at long.ts:80",
    );
  }

  test("the first step to a folded line opens the fold and shows the hint's popover", async ({
    page,
    weavie,
  }) => {
    await hintedSession(page, () => sessionWorktrees(weavie.workspace)[0]!);
    await runCommand(page, "Diff Against…");
    await page.locator(".session-prompt-input").fill("HEAD^");
    await page.locator(".session-prompt-input").press("Enter");
    // Diff Against opens its first file; the folding is in the unified review.
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 30_000 });
    await runCommand(page, "Show Session Overview");
    await expect(page.locator(".unified-review")).toBeVisible();

    await runCommand(page, "Next Comment or Hint");
    await expect(page.locator(".view-line", { hasText: "// line 80" })).toBeVisible();
    await expect(popover(page).locator(".weavie-hint-title")).toBeVisible();
    await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Folded hint");
  });

  test("a step in a file tab lands where the hint sits after edits above it", async ({
    page,
    weavie,
  }) => {
    await hintedSession(page, () => sessionWorktrees(weavie.workspace)[0]!);
    await openFile(page, "long.ts");
    await clickIntoEditor(page);
    await page.keyboard.press("ControlOrMeta+Home");
    await page.keyboard.type("// added\n// added\n// added\n");
    // The hint's mark moves with the edit at once; the step goes there, not to the line the hint was made on.
    expect(await hintLine(page)).toBe(83);
    await next(page);
    await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Folded hint");
    await expect.poll(() => lineNumber(page)).toBe(83);
  });
});

test.describe("dismissing as you go", () => {
  const addReviewHint = (line: number, text: string): FakeStep => ({
    op: "mcp",
    tool: "addReviewHint",
    args: { path: "long.ts", line, severity: "bug", text },
  });
  test.use({
    prScenario: false,
    fakeScript: {
      steps: [
        addReviewHint(4, "Hint four"),
        addReviewHint(8, "Hint eight"),
        addReviewHint(12, "Hint twelve"),
        addReviewHint(16, "Hint sixteen"),
      ],
    },
  });

  test("after a dismiss, Next goes on to the hint after the dismissed one", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "long.ts");
    await expect.poll(() => weavie.fakeLog().match(/^addReviewHint -> /gm)?.length ?? 0).toBe(4);
    await expect(page.locator(".weavie-hint-pill")).toHaveCount(4);
    const title = popover(page).locator(".weavie-hint-title");
    await runCommand(page, "Next Comment or Hint");
    await expect(title).toHaveText("Hint four");
    await next(page);
    await next(page);
    await expect(title).toHaveText("Hint twelve");

    await popover(page).getByRole("button", { name: "Dismiss hint" }).click();
    await expect(popover(page)).toHaveCount(0);
    await next(page);
    await expect(title).toHaveText("Hint sixteen");
    await expect(popover(page).locator(".review-item-position > span")).toHaveText("3 of 3");
  });
});

test("the header's text shares one baseline, Review's count sits beside its icon, and file actions answer hover", async ({
  page,
}) => {
  test.slow();
  // A pending review, so Review carries a count.
  await openCommentedFile(page);
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+Home");
  for (let i = 1; i < 6; i++) await page.keyboard.press("ArrowDown");
  await runCommand(page, "Comment on Line (Pull Request)");
  const draft = page.locator(".weavie-pr-card-draft");
  await draft.locator(".weavie-pr-composer-input").fill("Queued for the review.");
  await draft.getByRole("button", { name: "Start review", exact: true }).click();
  await expect(prCard(page, "Queued for the review.")).toBeVisible({ timeout: 10_000 });

  const header = page.locator(".unified-review-header");
  // A text item's baseline: the bottom of an empty inline-block placed in it.
  const baselines = (selectors: string[]) =>
    header.evaluate(
      (root, sels) =>
        sels.map((sel) => {
          const probe = document.createElement("span");
          probe.style.cssText = "display:inline-block;width:0;height:0";
          root.querySelector(sel)!.prepend(probe);
          const y = probe.getBoundingClientRect().bottom;
          probe.remove();
          return y;
        }),
      selectors,
    );
  const onOneBaseline = (values: number[]) =>
    expect(Math.max(...values) - Math.min(...values)).toBeLessThanOrEqual(0.5);

  await runCommand(page, "Show Session Overview");
  await expect(header.locator(".unified-review-kicker")).toBeVisible();
  onOneBaseline(
    await baselines([
      ".unified-review-kicker",
      "strong",
      ".unified-review-added",
      ".review-item-count",
    ]),
  );

  await runCommand(page, "Next Comment or Hint");
  await expect(header.locator(".unified-review-identity-title")).toBeVisible();
  onOneBaseline(
    await baselines([
      ".unified-review-identity-title",
      ".unified-review-status",
      ".unified-review-failing",
      ".review-item-count",
    ]),
  );

  // The state pill is a box, centred on the title's line.
  const centre = (box: { y: number; height: number }) => box.y + box.height / 2;
  const pill = (await header.locator(".session-overview-pill").boundingBox())!;
  const title = (await header.locator(".unified-review-identity-title").boundingBox())!;
  expect(Math.abs(centre(pill) - centre(title))).toBeLessThanOrEqual(0.5);

  const review = header.locator(".finish-review-button");
  await expect(review.locator(".finish-review-count")).toHaveText("1");
  const icon = (await review.locator("svg").boundingBox())!;
  const count = (await review.locator(".finish-review-count").boundingBox())!;
  const bar = (await header.boundingBox())!;
  expect(count.x).toBeGreaterThanOrEqual(icon.x + icon.width);
  expect(count.y).toBeGreaterThan(bar.y + 2);

  const keepFile = page.locator(".unified-review-file-action.keep").first();
  const rest = await keepFile.evaluate((element) => getComputedStyle(element).borderTopColor);
  await keepFile.hover();
  await expect
    .poll(() => keepFile.evaluate((element) => getComputedStyle(element).borderTopColor))
    .not.toBe(rest);
});
