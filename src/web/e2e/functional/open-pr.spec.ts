import { basename } from "node:path";
import type { Page } from "@playwright/test";
import {
  activeSessionSlot,
  createSession,
  openCommandPalette,
  runCommand,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { createInfraRepo } from "../harness/git-workspace";
import { focusEditor, navChord, stepIntoReview, walkToChangedFile } from "../harness/navigator";
import { decodeTestWebSocketMessage, encodeTestWebSocketMessage } from "../harness/websocket-codec";

// The Open-PR journey end to end against a stubbed PR provider + a local "origin" with a base/head diff
// (prScenario): pick the PR, check out its branch as a session, and walk its base→head diff in the inline-diff
// navigator. See docs/specs/open-pr.md (Phase 2).
test.use({ prScenario: true });

test("File → Open Pull Request checks out its branch and pops up the diff navigator", async ({
  page,
}) => {
  // The repo starts with one session on the workspace checkout.
  await expect(page.locator(".session-chip")).toHaveCount(1);

  await page.getByRole("menuitem", { name: "File", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: /^Open Current Pull Request/ })).toBeDisabled();
  const openPr = page.getByRole("menuitem", { name: /^Open Pull Request…/ });
  const modifier = process.platform === "darwin" ? "⌘" : "Ctrl";
  await expect(openPr.locator(".context-menu-keys")).toHaveText(`${modifier}+Shift+R`);
  await openPr.click();
  await expect(page.locator(".session-prompt")).toBeVisible();
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();

  // Pick it (Enter on the highlighted row) → a busy spinner toast names the PR while the host does its silent
  // fetch/checkout/seed chain, then a second session lands on the rail, on the PR's head branch.
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".toast-busy", { hasText: "#101" })).toBeVisible();
  await expect(page.locator(".session-chip")).toHaveCount(2, { timeout: 20_000 });

  // The review opens on its session overview; a nav key steps into the PR's first changed file — feature.ts, a new
  // file, so it shows the "New file" band rather than a per-line green wash.
  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(page.locator(".unified-review .session-overview")).toBeVisible({ timeout: 20_000 });

  // The correlated open result clears the spinner after the exact PR session has been selected.
  await expect(page.locator(".toast-busy")).toHaveCount(0, { timeout: 20_000 });
  expect(await stepIntoReview(page)).toBe("feature.ts");
  await expect(page.locator(".weavie-inline-newfile-tag")).toBeVisible();

  // Review Changes hydrates the complete file list while the file tab retains its own state.
  await page.locator(".editor-review-open").click();
  const overview = page.locator(".unified-review");
  await expect(overview.locator(".unified-review-file")).toHaveCount(2);
  await expect(
    overview.locator(".unified-review-file-name", { hasText: "feature.ts" }),
  ).toBeVisible();
  await expect(
    overview.locator(".unified-review-file-name", { hasText: "hello.ts" }),
  ).toBeVisible();
  await expect(overview.locator(".unified-review-notice", { hasText: "Loading" })).toHaveCount(0, {
    timeout: 20_000,
  });
  await overview
    .locator(".unified-review-file", { hasText: "feature.ts" })
    .locator(".line-numbers")
    .first()
    .click();
  await expect(overview).toHaveCount(0);
  await expect(page.locator(".editor-tab", { hasText: "Review Changes" })).toBeVisible();
  await expect(toolbar).toBeVisible();

  // It's a two-file walk (feature.ts added, hello.ts modified): the stacked label names the current file and
  // ← / → moves between them.
  const label = page.locator(".weavie-inline-stack-name");
  await expect(label).toHaveText(/feature\.ts|hello\.ts/);
  const first = (await label.textContent())?.trim() ?? "";
  await focusEditor(page); // the ← / → chord is `!terminalFocused`-guarded; a switch-in focuses Claude's terminal
  await page.keyboard.press(navChord("ArrowRight"));
  await expect
    .poll(async () => (await label.textContent())?.trim(), { timeout: 10_000 })
    .not.toBe(first);
  // The modified file (hello.ts) still shows the per-line added wash.
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  await page.getByRole("menuitem", { name: "File", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: /^Open Current Pull Request/ })).toBeEnabled();
  await page.keyboard.press("Escape");
  await page.getByRole("menuitem", { name: "Diff", exact: true }).click();
  await page.getByRole("menuitem", { name: /^Close Diff/ }).click();
  await expect(toolbar).toBeHidden();
});

test("typing #N opens a PR directly by number", async ({ page }) => {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".session-prompt")).toBeVisible();

  // Type the number directly — no dependence on the list (the host resolves its branch by number).
  await page.locator(".session-prompt-input").fill("#101");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  // The row previews the resolved PR's real title (debounced resolve-by-number).
  await expect(page.locator(".pr-suggestion-title", { hasText: "Add a feature" })).toBeVisible({
    timeout: 10_000,
  });
  await page.locator(".session-prompt-input").press("Enter");

  await expect(page.locator(".session-chip")).toHaveCount(2, { timeout: 20_000 });
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
});

test("a PR's review comments render on its changed file", async ({ page }) => {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({ timeout: 10_000 });
});

// The prompt belongs to the session it was opened for. A selection that changes while it is open (stood in for
// here by a click the modal would block) must not move the pull request into the newly selected session's repo.
test("a pull request opens in the repo of the session the prompt was opened for", async ({
  page,
  weavie,
}) => {
  const infra = createInfraRepo(weavie.workspace);
  const chips = page.locator(".session-chip");
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("e2e/deploy");
  await inbox
    .getByRole("combobox", { name: "Repository", exact: true })
    .selectOption({ label: "Other…" });
  const path = inbox.getByRole("textbox", { name: "Repository path" }).first();
  await path.fill(infra);
  await path.press("Enter");
  await expect(chips).toHaveCount(2);

  const home = chips.filter({ hasText: basename(weavie.workspace) });
  const other = chips.filter({ hasText: "infra" });
  await home.click();
  await expect(home).toHaveClass(/active/);
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();

  await other.dispatchEvent("click");
  await expect(other).toHaveClass(/active/);
  await page.locator(".session-prompt-input").press("Enter");

  await expect(chips).toHaveCount(3, { timeout: 20_000 });
  await expect(other).toHaveCount(1);
  await expect(home).toHaveCount(2);
});

// Unloads the session in `slot` from a second window on the same workspace, then waits for this window to see
// the selection leave it.
async function unloadFromAnotherWindow(page: Page, url: string, slot: string): Promise<void> {
  const other = await page.context().newPage();
  await other.goto(url, { waitUntil: "domcontentloaded" });
  await expect(other.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await other.locator(`.session-chip[data-session-slot="${slot}"]`).click({ button: "right" });
  await other.locator(".context-menu-item", { hasText: "Unload session" }).click();
  await expect(
    other.locator(`.session-chip:not(.unloaded)[data-session-slot="${slot}"]`),
  ).toHaveCount(0);
  await other.close();
  await expect(page.locator(`.session-chip.active[data-session-slot="${slot}"]`)).toHaveCount(0);
}

// The palette belongs to the session it was opened for: its rows and what they run are that session's. Opens it
// in the PR's session, then ends that session from another window while it is open; returns the bar's input.
async function paletteOutlivingItsSession(page: Page, url: string) {
  await runCommand(page, "Open Pull Request…");
  await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();
  await page.locator(".session-prompt-input").press("Enter");
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
  await walkToChangedFile(page, "hello.ts");
  await expect(
    page.locator(".weavie-pr-comment-body", { hasText: "Why change this greeting?" }),
  ).toBeVisible({ timeout: 10_000 });

  const prSlot = await activeSessionSlot(page);
  await openCommandPalette(page);
  const input = page.locator(".tb-omnibar-input");
  await input.fill(">Finish Review");
  await expect(
    page
      .locator(".tb-omnibar-row")
      .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Finish Review…$/ }) }),
  ).toHaveCount(1);

  await unloadFromAnotherWindow(page, url, prSlot);
  await expect(page.locator(".tb-omnibar-box")).not.toHaveClass(/\bopen\b/);
  await expect(
    page.locator(".toast", { hasText: "Command bar closed: its session ended." }),
  ).toBeVisible();
  await expect(input).toHaveValue("");
  // The input keeps focus, so the next click or keystroke reaches it with no new focus event.
  await expect(input).toBeFocused();
  return input;
}

// Opening it again belongs to the session now on screen: Open Pull Request opens there.
const opensForTheShownSession = (page: Page) =>
  expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();

test("a click reopens a palette whose session ended, for the session now on screen", async ({
  page,
  weavie,
}) => {
  const input = await paletteOutlivingItsSession(page, weavie.url);
  await input.click();
  await expect(page.locator(".tb-omnibar-box")).toHaveClass(/\bopen\b/);
  await input.fill(">Open Pull Request");
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Open Pull Request…$/ }) })
    .click();
  await opensForTheShownSession(page);
});

test("typing reopens a palette whose session ended, for the session now on screen", async ({
  page,
  weavie,
}) => {
  await paletteOutlivingItsSession(page, weavie.url);
  await page.keyboard.type(">Open Pull Request…");
  await page.keyboard.press("Enter");
  await opensForTheShownSession(page);
});

test.describe("pull request lookups that fail", () => {
  // The reply each failing lookup gets instead of the host's.
  const errors: Record<string, string> = {
    list: "GitHub search failed.",
    resolve: "GitHub lookup failed.",
  };
  const lookups = { drop: false, fail: false, dropped: 0, cancelled: 0 };
  const isLookup = (message: { feature?: string; name?: string }): boolean =>
    message.feature === "pullRequests" && (message.name === "list" || message.name === "resolve");
  test.use({
    preNavigate: {
      run: async (page) => {
        await page.routeWebSocket("**/*", (socket) => {
          const server = socket.connectToServer();
          const failing = new Map<string, string>();
          socket.onMessage((data) => {
            const message = JSON.parse(decodeTestWebSocketMessage(data));
            if (isLookup(message) && message.kind === "cancel") lookups.cancelled += 1;
            if (isLookup(message) && message.kind === "request" && lookups.drop) {
              lookups.dropped += 1;
              return;
            }
            if (isLookup(message) && lookups.fail)
              failing.set(message.requestId, errors[message.name] ?? "");
            server.send(data);
          });
          server.onMessage((data) => {
            const message = JSON.parse(decodeTestWebSocketMessage(data));
            const error = failing.get(message.requestId);
            if (error === undefined) return socket.send(data);
            failing.delete(message.requestId);
            message.error = error;
            socket.send(encodeTestWebSocketMessage(JSON.stringify(message)));
          });
        });
      },
    },
  });
  test.beforeEach(() => {
    Object.assign(lookups, { drop: false, fail: false, dropped: 0, cancelled: 0 });
  });

  // A failed lookup says why and lists nothing: no rows from the query before it, no loading text, no empty hint.
  test("an open prompt shows why its lookup failed, and nothing from before it", async ({
    page,
  }) => {
    const prompt = page.locator(".session-prompt");
    const error = prompt.locator(".session-prompt-error");
    const rows = prompt.locator(".session-prompt-suggestion");
    const expectFailed = async (message: string): Promise<void> => {
      await expect(error).toHaveText(message);
      await expect(rows).toHaveCount(0);
      await expect(prompt).not.toContainText("Searching pull requests…");
      await expect(prompt).not.toContainText("Resolving…");
      await expect(prompt).not.toContainText("No matching pull requests");
    };
    await runCommand(page, "Open Pull Request…");
    await expect(page.locator(".pr-suggestion-number", { hasText: "#101" })).toBeVisible();

    lookups.fail = true;
    await page.locator(".session-prompt-input").fill("greeting");
    await expectFailed("GitHub search failed.");
    await page.locator(".session-prompt-input").fill("#7");
    await expectFailed("GitHub lookup failed.");
  });

  // A lookup left without a reply is cancelled when the prompt closes, with its session still live.
  test("closing the prompt cancels its pending lookup", async ({ page }) => {
    lookups.drop = true;
    await runCommand(page, "Open Pull Request…");
    await expect.poll(() => lookups.dropped).toBe(1);
    await page.locator(".session-prompt-input").press("Escape");
    await expect(page.locator(".session-prompt")).toBeHidden();
    await expect.poll(() => lookups.cancelled).toBe(1);
  });

  test("a prompt closed by its session ending drops its pending lookups quietly", async ({
    page,
    weavie,
  }) => {
    const errors: string[] = [];
    page.on("pageerror", (error) => errors.push(String(error)));
    await createSession(page, { branch: "e2e/pr-lookup-owner", provider: "claude" });
    const owner = await activeSessionSlot(page);
    lookups.drop = true;
    await runCommand(page, "Open Pull Request…");
    await expect.poll(() => lookups.dropped).toBe(1);
    await page.locator(".session-prompt-input").fill("#7");
    await expect.poll(() => lookups.dropped).toBe(2);

    await unloadFromAnotherWindow(page, weavie.url, owner);
    await expect(page.locator(".session-prompt")).toBeHidden();
    await expect(
      page.locator(".toast", { hasText: "Open Pull Request closed: its session ended." }),
    ).toBeVisible();
    await page.evaluate(() => new Promise((settled) => setTimeout(settled, 0)));
    expect(errors).toEqual([]);
  });
});
