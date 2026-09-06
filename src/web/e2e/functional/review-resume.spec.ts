import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { openFile, runCommand } from "../harness/actions";
import { writeFakeScript } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import type { HeadlessHost } from "../harness/weavie-host";
import type { WeavieWindow } from "../harness/weavie-window";
import { parseEnvelope, type MessageEnvelope } from "../../src/messaging/message-envelope";

const prReplies = new WeakMap<Page, MessageEnvelope[]>();

const HELLO =
  "export function greet(name: string): string {\n" +
  "  return `Review kept this greeting, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";
const NOTES = "just plain text\nthis change is still pending\n";
const NEXT = "# New turn\n\nThis proposal must not reset earlier decisions.\n";
const SIGNAL = ".weavie-e2e-next-review-turn";

const section = (page: Page, name: string): Locator =>
  page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: name }),
  });

async function openPr(page: Page): Promise<void> {
  await runCommand(page, "Open Pull Request");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  const replies = prReplies.get(page)!;
  const expected = replies.length + 1;
  await page.locator(".session-prompt-input").press("Enter");
  await expect.poll(() => replies.length).toBe(expected);
  expect(replies.at(-1)?.error).toBeNull();
  expect(replies.at(-1)?.payload, JSON.stringify(replies.at(-1))).toMatchObject({ ok: true });
  await expect(page.locator(".toast-busy")).toHaveCount(0);
}

test.describe("durable applied review", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", HELLO),
        ...appliedEdit("notes.txt", NOTES),
        { op: "waitFile", path: `{{WORKSPACE}}/${SIGNAL}` },
        { op: "hook", request: { hook_event_name: "UserPromptSubmit" } },
        ...appliedEdit("README.md", NEXT),
      ],
    },
  });

  test("new turns, unloads, and host restarts resume decisions and the review view", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(".weavie-inline-pending-keep")).toHaveCount(2);
    await page.locator(".weavie-inline-pending-keep").first().click();
    await expect(page.locator(".weavie-inline-accepted")).toHaveCount(1);
    await page.locator(".weavie-inline-pending-revert").click();
    await expect
      .poll(() => readFile(join(weavie.workspace, "hello.ts"), "utf8"))
      .toBe(HELLO.replace("console.warn", "console.log"));
    await expect(page.locator(".weavie-inline-hist").first()).toBeEnabled();

    await writeFile(join(weavie.workspace, SIGNAL), "");
    await expect.poll(() => readFile(join(weavie.workspace, "README.md"), "utf8")).toBe(NEXT);
    await openFile(page, "hello.ts");
    await expect(page.locator(".weavie-inline-accepted")).toHaveCount(1);
    await page.locator(".editor-review-toggle").click();
    const hello = section(page, "hello.ts");
    await expect(hello.locator(".unified-review-file-toggle")).toHaveAttribute("aria-expanded", "false");
    await hello.locator(".unified-review-file-toggle").click();
    await expect(hello.locator(".unified-review-rejection pre")).toHaveText("console.warn(message);");
    const notes = section(page, "notes.txt");
    await expect(notes.locator(".weavie-inline-added")).toBeVisible();
    await notes.locator(".unified-review-file-toggle").click();
    await expect(notes.locator(".unified-review-file-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    await expect(section(page, "README.md").locator(".weavie-inline-added").first()).toBeVisible();

    // The next process must not replay the scripted edits: restoration is the only source of this review.
    await writeFakeScript(weavie.home, []);
    await runCommand(page, "Unload Session");
    await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);
    await page.locator(".session-chip.unloaded").click();
    await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
    await expect(page.locator(".unified-review")).toBeVisible();
    await expect(notes.locator(".unified-review-file-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );

    // This is a new host process, not a browser reload or reconnect to a live tracker.
    await page.goto("about:blank");
    await (weavie as HeadlessHost).restart();
    const connect = await page.request.post(weavie.url, {
      form: { token: weavie.token },
      maxRedirects: 0,
    });
    expect(connect.status()).toBe(302);
    await page.goto(weavie.url);
    await expect(page.locator("#splash")).toHaveCount(0);
    await expect(page.locator(".unified-review")).toBeVisible();
    await expect(notes.locator(".unified-review-file-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    await expect(section(page, "README.md").locator(".weavie-inline-added").first()).toBeVisible();

    await page.locator(".editor-review-toggle").click();
    await expect(page.locator(".editor-tab.active", { hasText: "notes.txt" })).toBeVisible();
    await expect
      .poll(() =>
        page.evaluate(() => (window as WeavieWindow).__WEAVIE_EDITOR__?.getPosition()?.lineNumber),
      )
      .toBe(2);
    await page.locator(".editor-review-toggle").click();

    await section(page, "hello.ts").locator(".unified-review-file-name").click();
    await expect(page.locator(".weavie-inline-accepted")).toHaveCount(1);
    await runCommand(page, "Undo Revert (Review)");
    await expect.poll(() => readFile(join(weavie.workspace, "hello.ts"), "utf8")).toBe(HELLO);
    await expect(page.locator(".weavie-inline-added")).toHaveCount(1);
    await page.locator(".weavie-inline-accepted-undo").click();
    await expect(page.locator(".weavie-inline-added")).toHaveCount(2);
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(NOTES);
    expect(await readFile(join(weavie.workspace, "README.md"), "utf8")).toBe(NEXT);
  });
});

test.describe("durable pull-request review", () => {
  test.use({ prScenario: true, preNavigate: { run: async (page) => {
    const replies: MessageEnvelope[] = [];
    prReplies.set(page, replies);
    page.on("websocket", socket => socket.on("framereceived", frame => {
      const message = parseEnvelope(frame.payload.toString());
      if (message?.kind === "response" && message.feature === "pullRequests" && message.name === "open") replies.push(message);
    }));
  } } });

  test("opening the same pull request retains its kept decision and undo", async ({ page }) => {
    await openPr(page);
    await expect(page.locator(".session-chip")).toHaveCount(2);
    await expect(page.locator(".editor-review-toggle")).toBeVisible();
    await page.locator(".editor-review-toggle").click();
    const hello = section(page, "hello.ts");
    await hello.locator(".unified-review-file-action.keep").click();
    await expect(hello.locator(".unified-review-status")).toHaveText("Reviewed");

    await openPr(page);
    await expect(page.locator(".session-chip")).toHaveCount(2);
    await expect(page.locator(".unified-review")).toBeVisible();
    await expect(hello.locator(".unified-review-status")).toHaveText("Reviewed");
    await expect(hello.locator(".unified-review-file-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    await hello.locator(".unified-review-file-name").click();
    await expect(page.locator(".weavie-inline-accepted")).toHaveCount(2);
    await expect(page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" })).toBeVisible();
    await runCommand(page, "Undo Keep (Review)");
    await expect(page.locator(".weavie-inline-added")).toHaveCount(2);
  });
});
