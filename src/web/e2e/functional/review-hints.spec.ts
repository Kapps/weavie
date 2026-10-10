import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page, WebSocketRoute } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { openCommandPalette, openFile, runCommand } from "../harness/actions";
import { type FakeStep, writeFakeScript } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import type { HeadlessHost } from "../harness/weavie-host";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

// Inline review hints: agent findings captured from Claude's ReportFindings hook or the addReviewHint tool render as
// a badge, wash and pill in every editor, open a popover, and send Fix prompts to the agent. See
// docs/specs/inline-review-hints.md.

const reportFindings = (findings: Record<string, unknown>[]): FakeStep => ({
  op: "hook",
  request: {
    hook_event_name: "PostToolUse",
    tool_name: "ReportFindings",
    cwd: "{{WORKSPACE}}",
    tool_input: { findings },
  },
});

const addReviewHint = (path: string, line: number, severity: string, text: string): FakeStep => ({
  op: "mcp",
  tool: "addReviewHint",
  args: { path, line, severity, text },
});

const popover = (page: Page): Locator => page.locator(".weavie-hint-hover");

// The fake logs each line typed into Claude's input; a submitted prompt arrives as one bracketed paste.
const fixPrompt = (at: string): RegExp =>
  new RegExp(`^input \\x1b\\[200~Fix this review finding at \`${at.replace(".", "\\.")}\`:$`, "m");

// The view line showing `text`, and the hint wash Monaco paints on the same row.
async function hintRowOffset(scope: Locator, text: string): Promise<number> {
  const line = await scope.locator(".view-line", { hasText: text }).first().boundingBox();
  const wash = await scope.locator(".weavie-hint-line").first().boundingBox();
  return Math.round((wash?.y ?? Number.NaN) - (line?.y ?? Number.NaN));
}

test.describe("ReportFindings", () => {
  const CHANGED = 'export const greeting = "hi";\nexport const name = "weavie";\n';
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("notes.txt", CHANGED),
        reportFindings([
          {
            file: "notes.txt",
            line: 2,
            summary: "The exported name shadows the module's default export",
            short_summary: "Name shadows the default export",
            failure_scenario: "Importers that expect the default get the string instead.",
            verdict: "PLAUSIBLE",
            category: "correctness",
          },
        ]),
      ],
    },
  });

  test("renders in a plain tab and turn review with a popover advertising catalog shortcuts", async ({
    page,
    weavie,
  }) => {
    await writeFile(
      join(weavie.home, ".weavie", "keybindings.json"),
      JSON.stringify([{ key: "ctrl+alt+shift+f", command: "weavie.reviewHints.fix" }]),
    );
    // Tooltips read the catalog when the popover renders, so wait until the palette shows the new binding.
    await openCommandPalette(page);
    await page.locator(".tb-omnibar-input").fill(">Fix Review Hint");
    await expect(
      page
        .locator(".tb-omnibar-row")
        .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Fix Review Hint$/ }) })
        .locator(".tb-row-keys"),
    ).toHaveText("Ctrl+Alt+Shift+F");
    await page.keyboard.press("Escape");
    await openFile(page, "notes.txt");
    const editor = page.locator(".editor-surface .monaco-editor").first();

    await test.step("the plain tab shows the badge, wash and pill", async () => {
      await expect(editor.locator(".weavie-hint-mark.weavie-hint-warn")).toHaveCount(1);
      await expect(editor.locator(".weavie-hint-line.weavie-hint-warn")).toHaveCount(1);
      await expect(editor.locator(".weavie-hint-pill")).toHaveText(
        "✦ Warning Name shadows the default export",
      );
      await expect.poll(() => hintRowOffset(editor, 'export const name = "weavie";')).toBe(0);
    });

    await test.step("hovering the pill opens the popover with the finding", async () => {
      await editor.locator(".weavie-hint-pill").hover();
      await expect(popover(page).locator(".weavie-hint-title")).toHaveText(
        "Name shadows the default export",
      );
      await expect(popover(page).locator(".weavie-hint-text")).toContainText(
        "Importers that expect the default get the string instead.",
      );
      await expect(popover(page).locator(".weavie-hint-meta")).toContainText("✦ Claude Code");
      await expect(popover(page).locator(".weavie-hint-meta")).toContainText("correctness");
      await expect(popover(page).getByRole("button", { name: "Fix", exact: true })).toHaveAttribute(
        "title",
        "Fix (Ctrl+Alt+Shift+F)",
      );
      await expect(popover(page).getByRole("button", { name: "Dismiss hint" })).toHaveAttribute(
        "title",
        "Dismiss hint",
      );
    });

    await test.step("the turn review shows it, and clicking the badge pins the popover", async () => {
      await page.mouse.move(0, 0);
      await expect(popover(page)).toHaveCount(0);
      await page.locator(".editor-review-open").click();
      const review = page.locator(".unified-review");
      await expect(review.locator(".weavie-inline-added").first()).toBeVisible();
      // Clicking the code (not its blame annotation) gives the review editor focus.
      await review
        .locator(".view-line", { hasText: 'export const greeting = "hi";' })
        .click({ position: { x: 4, y: 4 } });
      await review.locator(".weavie-hint-mark").click();
      await expect(popover(page).locator(".weavie-hint-title")).toHaveText(
        "Name shadows the default export",
      );
      await page.mouse.move(0, 0);
      await expect(popover(page)).toBeVisible();
    });

    await test.step("Escape closes the editor's find widget before the popover", async () => {
      await page.keyboard.press("ControlOrMeta+f");
      const find = page.locator(".unified-review .find-widget.visible");
      await expect(find).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(find).toHaveCount(0);
      await expect(popover(page)).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(popover(page)).toHaveCount(0);
    });
  });
});

test.describe("a long title", () => {
  const SUMMARY =
    "The discount `percent` is no longer divided by 100, so `total - percent` subtracts whole units";
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("notes.txt", "// pricing\nconst discount = total - percent;\n"),
        reportFindings([
          {
            file: "notes.txt",
            line: 2,
            summary: SUMMARY,
            verdict: "CONFIRMED",
            category: "correctness",
          },
        ]),
      ],
    },
  });

  test("renders as one ellipsized pill with code spans, and the popover header stays on one line", async ({
    page,
  }) => {
    await openFile(page, "notes.txt");
    const editor = page.locator(".editor-surface .monaco-editor").first();
    const pill = editor.locator(".weavie-hint-pill");
    // The cursor rests on line 1, so the blame annotation sits there rather than before the pill.
    await expect(pill).toHaveCount(1);
    await expect(pill).toBeVisible();
    await expect(pill.locator("code")).toHaveText(["percent", "total - percent"]);
    await expect(pill).not.toContainText("`");
    expect(
      await pill.evaluate(
        (element) =>
          getComputedStyle(element).textOverflow === "ellipsis" &&
          element.scrollWidth > element.clientWidth,
      ),
    ).toBe(true);
    const viewport = await editor
      .locator(".monaco-scrollable-element.editor-scrollable")
      .boundingBox();
    const box = await pill.boundingBox();
    expect((box?.x ?? Number.NaN) + (box?.width ?? Number.NaN)).toBeLessThanOrEqual(
      (viewport?.x ?? Number.NaN) + (viewport?.width ?? Number.NaN),
    );

    await editor.locator(".view-line", { hasText: "discount" }).click({ position: { x: 4, y: 4 } });
    await page.keyboard.press("End");
    await page.keyboard.type(" // edited");
    await expect(editor.locator(".weavie-hint-line.weavie-hint-changed")).toHaveCount(1);
    await editor.locator(".weavie-hint-mark").click();
    await expect(popover(page).locator(".weavie-hint-title code")).toHaveText([
      "percent",
      "total - percent",
    ]);
    const meta = popover(page).locator(".weavie-hint-meta");
    await expect(meta).toContainText("Changed");
    // All the header's text shares one line, and no label is truncated.
    const header = await meta.evaluate((root) => {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const middles = new Set<number>();
      for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
        const range = document.createRange();
        range.selectNodeContents(node);
        for (const rect of range.getClientRects())
          middles.add(Math.round(rect.top + rect.height / 2));
      }
      const truncated = [...root.querySelectorAll(":scope > b, :scope > span")].filter(
        (label) => label.scrollWidth > label.clientWidth,
      );
      return { spread: Math.max(...middles) - Math.min(...middles), truncated: truncated.length };
    });
    expect(header.spread).toBeLessThanOrEqual(2);
    expect(header.truncated).toBe(0);
  });
});

test.describe("addReviewHint from Claude", () => {
  const STEPS = "one\ntwo\nthree\n\nfive is the target\nsix\n";
  test.use({
    // A committed plain-text file, so Diff Against has a base and no language-server toast covers the editor.
    workspaceSeed: {
      async run(workspace) {
        await writeFile(join(workspace, "steps.txt"), STEPS);
        for (const args of [
          ["add", "steps.txt"],
          ["commit", "-q", "-m", "steps"],
        ]) {
          execFileSync("git", args, { cwd: workspace, stdio: "ignore" });
        }
      },
    },
    fakeScript: {
      steps: [addReviewHint("steps.txt", 5, "bug", "Target is vague\nName what it targets.")],
    },
  });

  test("anchors in Diff Against: an edit above moves it, editing its line fades it", async ({
    page,
    weavie,
  }) => {
    // The hint is captured against the commit; an uncommitted change elsewhere in the file puts it in the diff.
    await openFile(page, "steps.txt");
    await expect(page.locator(".weavie-hint-mark.weavie-hint-bug")).toHaveCount(1);
    await writeFile(join(weavie.workspace, "steps.txt"), STEPS.replace("two", "TWO"));
    await runCommand(page, "Diff Against HEAD");
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText("steps.txt", {
      timeout: 30_000,
    });
    const review = page.locator(".editor-surface .monaco-editor").first();
    const target = "five is the target";
    await expect(review.locator(".weavie-hint-pill")).toHaveText("✦ Bug Target is vague");
    await expect.poll(() => hintRowOffset(review, target)).toBe(0);

    await test.step("a line inserted above carries the hint down with its text", async () => {
      await review.locator(".view-line", { hasText: /^six$/ }).click({ position: { x: 4, y: 4 } });
      await page.keyboard.press("Home");
      await page.keyboard.press("ArrowUp");
      await page.keyboard.press("ArrowUp");
      await page.keyboard.press("End");
      await page.keyboard.press("Enter");
      await page.keyboard.type("inserted above");
      await expect(review.locator(".view-line", { hasText: "inserted above" })).toBeVisible();
      await expect.poll(() => hintRowOffset(review, target)).toBe(0);
      await expect(review.locator(".weavie-hint-changed")).toHaveCount(0);
    });

    await test.step("editing the hint's own line fades it", async () => {
      await review.locator(".view-line", { hasText: target }).click({ position: { x: 4, y: 4 } });
      await page.keyboard.press("End");
      await page.keyboard.type(" now");
      await expect(review.locator(".weavie-hint-line.weavie-hint-changed")).toHaveCount(1);
      await review.locator(".weavie-hint-mark").click();
      await expect(popover(page).locator(".weavie-hint-meta")).toContainText("Changed");
    });
  });
});

test.describe("PR review", () => {
  test.use({
    prScenario: true,
    fakeScript: {
      steps: [
        reportFindings([
          {
            file: "hello.ts",
            line: 5,
            summary: "The PR greeting is hardcoded",
            verdict: "CONFIRMED",
          },
        ]),
      ],
    },
  });

  test("renders a hint beside PR threads, and a thread's Fix asks the agent", async ({
    page,
    weavie,
  }) => {
    test.slow();
    await runCommand(page, "Open Pull Request…");
    await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
    await page.locator(".session-prompt-input").press("Enter");
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    await walkToChangedFile(page, "hello.ts");
    const thread = page.locator(".weavie-pr-card").filter({ hasText: "Why change this greeting?" });
    await expect(thread).toBeVisible({ timeout: 10_000 });
    const review = page.locator(".editor-surface .monaco-editor").first();
    await expect(review.locator(".weavie-hint-pill")).toHaveText(
      "✦ Bug The PR greeting is hardcoded",
    );
    await expect
      .poll(() => hintRowOffset(review, 'const message = greet("pull request");'))
      .toBe(0);

    await test.step("a live thread's Fix sends it to the agent", async () => {
      await thread.getByRole("button", { name: "Fix", exact: true }).click();
      await expect.poll(() => weavie.fakeLog()).toMatch(fixPrompt("hello.ts:2"));
      expect(weavie.fakeLog()).toMatch(/^input bob: Why change this greeting\?/m);
    });

    await test.step("an outdated thread offers no Fix", async () => {
      await runCommand(page, "Open Pull Request…");
      await page.locator(".session-prompt-input").fill("#102");
      await expect(page.locator(".pr-suggestion-number", { hasText: "#102" })).toBeVisible();
      await page.locator(".session-prompt-input").press("Enter");
      await expect(page.locator(".session-chip")).toHaveCount(3, { timeout: 25_000 });
      await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
      await walkToChangedFile(page, "widget.ts");
      const outdated = page.locator(".weavie-pr-card").filter({ hasText: "Is 42 the agreed" });
      // Its right end: toasts stack over the middle of the editor's first lines.
      const summary = outdated.locator(".weavie-pr-summary");
      const box = await summary.boundingBox();
      await summary.click({ position: { x: (box?.width ?? 0) - 12, y: (box?.height ?? 0) / 2 } });
      await expect(outdated.getByRole("button", { name: "Collapse thread" })).toBeVisible();
      await expect(outdated.getByRole("button", { name: /^Fix/ })).toHaveCount(0);
    });
  });
});

test.describe("across files", () => {
  // Plain-text files: no language-server toast lands over the editor's first lines.
  test.use({
    workspaceSeed: {
      run: (workspace) => writeFile(join(workspace, "todo.txt"), "first\nsecond\nthird\n"),
    },
    fakeScript: {
      steps: [
        addReviewHint("notes.txt", 1, "bug", "Text is too plain\nSay what the notes are for."),
        addReviewHint("todo.txt", 2, "nit", "Second is vague"),
      ],
    },
  });

  test("Next/Previous open across files; Fix with…, Toggle and Dismiss act on them", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "notes.txt");
    const mark = page.locator(".editor-surface .weavie-hint-mark");
    await expect(mark).toHaveCount(1);

    await test.step("Next and Previous walk the hints across files", async () => {
      await runCommand(page, "Next Review Hint");
      await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]todo\.txt$/);
      await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Second is vague");
      await runCommand(page, "Previous Review Hint");
      await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]notes\.txt$/);
      await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Text is too plain");
    });

    await test.step("Fix with… sends the typed instruction", async () => {
      await popover(page).getByRole("button", { name: "Fix with…" }).click();
      const dialog = page.getByRole("dialog", { name: "Fix with…" });
      await expect(dialog).toContainText("How should the agent fix notes.txt:1?");
      const input = dialog.locator("input");
      await expect(input).toBeFocused();
      await input.fill("Keep it to one line");
      await input.press("Enter");
      await expect
        .poll(() => weavie.fakeLog())
        .toMatch(/^input Additional instruction: Keep it to one line/m);
      expect(weavie.fakeLog()).toMatch(fixPrompt("notes.txt:1"));
      expect(weavie.fakeLog()).toMatch(/^input Say what the notes are for\./m);
      const sent = page.locator(".toast", { hasText: "Asked the agent to fix notes.txt:1." });
      await sent.locator(".toast-close").click();
      await expect(sent).toHaveCount(0);
    });

    await test.step("Toggle hides and shows every hint", async () => {
      await runCommand(page, "Toggle Review Hints");
      await expect(mark).toHaveCount(0);
      await runCommand(page, "Toggle Review Hints");
      await expect(mark).toHaveCount(1);
    });

    await test.step("Dismiss removes the hint", async () => {
      await mark.click();
      await popover(page).getByRole("button", { name: "Dismiss hint" }).click();
      await expect(mark).toHaveCount(0);
      await expect(popover(page)).toHaveCount(0);
      await runCommand(page, "Next Review Hint");
      await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[\\/]todo\.txt$/);
      await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Second is vague");
    });
  });
});

test.describe("persistence", () => {
  test.use({ fakeScript: { steps: [addReviewHint("hello.ts", 2, "warn", "Persisted hint")] } });

  test("hints replay after a page reload and a host restart", async ({ page, weavie }) => {
    test.slow();
    await openFile(page, "hello.ts");
    const pill = page.locator(".weavie-hint-pill");
    await expect(pill).toHaveText("✦ Warning Persisted hint");

    await test.step("page reload", async () => {
      await page.reload({ waitUntil: "domcontentloaded" });
      await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
      await openFile(page, "hello.ts");
      await expect(pill).toHaveText("✦ Warning Persisted hint");
    });

    await test.step("host restart", async () => {
      await writeFakeScript(weavie.home, []);
      await page.goto("about:blank");
      await (weavie as HeadlessHost).restart();
      const connect = await page.request.post(weavie.url, {
        form: { token: weavie.token },
        maxRedirects: 0,
      });
      expect(connect.status()).toBe(302);
      await page.goto(weavie.url);
      await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
      await openFile(page, "hello.ts");
      await expect(pill).toHaveText("✦ Warning Persisted hint");
    });
  });
});

test("an ACP agent's addReviewHint renders", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-review-hint");
  await submitAcpDraft(
    surface,
    `review-hint:${JSON.stringify({ path: "notes.txt", line: 1, severity: "bug", text: "ACP finding\nDetails" })}`,
  );
  await expect(surface).toContainText("review hint: Hint added at notes.txt:1.");
  await openFile(page, "notes.txt");
  await expect(page.locator(".weavie-hint-pill")).toHaveText("✦ Bug ACP finding");
  await page.locator(".weavie-hint-mark").click();
  await expect(popover(page).locator(".weavie-hint-meta")).toContainText("✦ Fake ACP");
});

test.describe("reconnect", () => {
  // Every bridge connection's decoded server frames, in connection order.
  const connections: { socket: WebSocketRoute; frames: string[] }[] = [];
  test.use({
    fakeScript: { steps: [addReviewHint("notes.txt", 1, "bug", "Survives reconnect")] },
    preNavigate: {
      async run(page) {
        connections.length = 0;
        await page.routeWebSocket(/weavie-bridge/, (socket) => {
          const connection = { socket, frames: [] as string[] };
          connections.push(connection);
          const server = socket.connectToServer();
          server.onMessage((data) => {
            connection.frames.push(decodeTestWebSocketMessage(data));
            socket.send(data);
          });
        });
      },
    },
  });

  test("hints replay on bridge reconnect @remote", async ({ page }) => {
    await openFile(page, "notes.txt");
    const pill = page.locator(".weavie-hint-pill");
    await expect(pill).toHaveText("✦ Bug Survives reconnect");
    const before = connections.length;
    await connections[before - 1]?.socket.close();
    await expect
      .poll(() =>
        connections
          .slice(before)
          .some(({ frames }) =>
            frames.some(
              (frame) =>
                frame.includes('"feature":"reviewHints"') && frame.includes("Survives reconnect"),
            ),
          ),
      )
      .toBe(true);
    await expect(page.locator(".footer-network-problem")).toHaveCount(0);
    await expect(pill).toHaveText("✦ Bug Survives reconnect");
    await page.locator(".weavie-hint-mark").click();
    await expect(popover(page).locator(".weavie-hint-title")).toHaveText("Survives reconnect");
  });
});
