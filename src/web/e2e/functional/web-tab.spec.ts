import { writeFile } from "node:fs/promises";
import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { canonicalFsPath } from "../../src/editor/fs-path";
import {
  activeSessionSlot,
  createSession,
  openFile,
  runCommand,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { persistedActiveTabs } from "../harness/persisted-sessions";

let server: Server;
let origin: string;
let loads: Map<string, number>;

test.beforeAll(async () => {
  loads = new Map();
  server = createServer((request, response) => {
    const path = request.url ?? "/";
    if (!path.startsWith("/form")) {
      response.writeHead(404).end();
      return;
    }
    const count = (loads.get(path) ?? 0) + 1;
    loads.set(path, count);
    response.writeHead(200, {
      "Cache-Control": "no-store",
      "Content-Type": "text/html; charset=utf-8",
    });
    response.end(`<!doctype html>
      <title>Stateful form</title>
      <p id="load-count">Page loads: ${count}</p>
      <label>Approval note <input aria-label="Approval note"></label>
      <button onclick="try { top.location.href='/form-escaped'; } catch (error) { this.textContent=error.name; }">Navigate top</button>`);
  });
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});

test.afterAll(async () => {
  await new Promise<void>((resolve, reject) => {
    server.close((error) => (error === undefined ? resolve() : reject(error)));
  });
});

async function openUrl(page: import("@playwright/test").Page, url: string): Promise<void> {
  await runCommand(page, "Open URL…");
  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill(url);
  await input.press("Enter");
  await expect(page.locator(".editor-tab.active")).toHaveAttribute("title", url);
}

function activeFrame(page: import("@playwright/test").Page) {
  return page.frameLocator(".editor-tab-content:not([hidden]) .editor-web iframe");
}

test("@cross Markdown preview links open externally without replacing the app", async ({
  page,
  weavie,
}) => {
  test.slow();
  const url = `${origin}/form-markdown`;
  await writeFile(
    join(weavie.workspace, "README.md"),
    `# Preview\n\n[Visit destination](${url})\n\n` +
      `<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="240" height="40"><a xlink:href="${url}"><text x="0" y="25">SVG destination</text></a></svg>\n\n` +
      '<a href="missing%ZZ.txt">Malformed path</a>\n',
  );
  await openFile(page, "README.md");
  await page.locator(".editor-preview-toggle").click();
  const appUrl = page.url();
  for (const link of [
    page.getByRole("link", { name: "Visit destination" }),
    page.locator(".editor-preview svg a"),
  ]) {
    const [popup] = await Promise.all([page.waitForEvent("popup"), link.click()]);
    await expect(popup).toHaveURL(url);
    await expect(popup.getByRole("textbox", { name: "Approval note" })).toBeVisible();
    expect(await popup.evaluate(() => window.opener)).toBeNull();
    await popup.close();
  }
  await page.getByRole("link", { name: "Malformed path" }).click();
  await expect(page.locator(".toast", { hasText: "Could not open link:" })).toBeVisible();
  await expect(page).toHaveURL(appUrl);
  await expect(page.locator(".editor-tab.active")).toHaveText(/README\.md/);
});

test("embedded web content cannot navigate the app's top frame", async ({ page }) => {
  const appUrl = page.url();
  expect((await page.request.get(appUrl)).headers()["content-security-policy"]).toBe(
    "frame-ancestors 'none'",
  );
  await openUrl(page, `${origin}/form-confined`);
  const frame = activeFrame(page);
  await frame.getByRole("button", { name: "Navigate top" }).click();
  await expect(frame.getByRole("button", { name: "SecurityError" })).toBeVisible();
  await expect(page).toHaveURL(appUrl);
  await frame.getByRole("textbox", { name: "Approval note" }).fill("Still usable");
  await expect(frame.getByRole("textbox", { name: "Approval note" })).toHaveValue("Still usable");
});

test("web tab retains its live page state across editor tabs until close", async ({ page }) => {
  const path = "/form-state";
  const url = `${origin}${path}`;
  await openUrl(page, url);

  const frame = activeFrame(page);
  await expect(frame.locator("#load-count")).toHaveText("Page loads: 1");
  await frame.getByRole("textbox", { name: "Approval note" }).fill("APPROVE CANARY");

  await openFile(page, "README.md");
  const footer = page.locator(".editor-surface .pane-footer");
  const cursor = footer.locator(".footer-seg", { hasText: /^Ln / });
  await expect(cursor).toBeVisible();
  await page.locator(".editor-preview-toggle").click();
  await expect(cursor).toHaveCount(0);
  await expect(footer.locator(".footer-recent-toggle")).toBeVisible();
  await page.locator(".editor-preview-toggle").click();
  await expect(cursor).toBeVisible();
  const retained = page.locator(".editor-web");
  await expect(retained).toBeHidden();
  const shell = page.locator(".editor-tab-content", { has: retained });
  await expect(shell).toHaveAttribute("inert", "");
  await retained.focus();
  await expect(retained).not.toBeFocused();
  await expect(retained.locator("iframe")).toHaveCount(1);

  await page.locator(".editor-tab", { hasText: new URL(url).host }).click();
  await expect(cursor).toHaveCount(0);
  await expect(footer.locator(".footer-recent-toggle")).toBeVisible();
  await expect(frame.locator("#load-count")).toHaveText("Page loads: 1");
  await expect(frame.getByRole("textbox", { name: "Approval note" })).toHaveValue("APPROVE CANARY");
  await expect(shell).not.toHaveAttribute("inert", "");
  await expect(retained).toHaveAttribute("tabindex", "0");

  await retained.focus();
  await expect(retained).toBeFocused();
  await page.keyboard.press("Control+Tab");
  await expect(page.locator(".editor-tab.active")).toHaveText(/README\.md/);
  await expect(cursor).toBeVisible();
  await expect
    .poll(() =>
      page.evaluate(
        () => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind") ?? null,
      ),
    )
    .toBe("editor");
  await page.locator(".editor-tab", { hasText: new URL(url).host }).click();
  await expect(frame.getByRole("textbox", { name: "Approval note" })).toHaveValue("APPROVE CANARY");

  await page.locator(".editor-tab.active .editor-tab-close").click();
  await expect(page.locator(".editor-web")).toHaveCount(0);
  await openUrl(page, url);
  await expect(activeFrame(page).locator("#load-count")).toHaveText("Page loads: 2");
  await expect(activeFrame(page).getByRole("textbox", { name: "Approval note" })).toHaveValue("");
  await runCommand(page, "Close All Editors");
  await expect(page.locator(".editor-empty")).toBeVisible();
  await expect(cursor).toHaveCount(0);
  await expect(footer.locator(".footer-recent-toggle")).toBeVisible();
});

test("same URL keeps isolated live state in two exact sessions", async ({ page }) => {
  const url = `${origin}/form-sessions`;
  await openUrl(page, url);
  await activeFrame(page).getByRole("textbox", { name: "Approval note" }).fill("FIRST SESSION");
  const firstSlot = await activeSessionSlot(page);

  await page.locator(".editor-tab.active .editor-tab-main").click();
  await createSession(page, { branch: "e2e/web-tab-isolation", provider: "claude" });
  const secondSlot = await waitForSessionSwitch(page, firstSlot);
  await openUrl(page, url);
  await expect(activeFrame(page).locator("#load-count")).toHaveText("Page loads: 2");
  await activeFrame(page).getByRole("textbox", { name: "Approval note" }).fill("SECOND SESSION");

  await page.locator(`.session-chip[data-session-slot="${firstSlot}"]`).click();
  await expect(activeFrame(page).getByRole("textbox", { name: "Approval note" })).toHaveValue(
    "FIRST SESSION",
  );
  await page.locator(`.session-chip[data-session-slot="${secondSlot}"]`).click();
  await expect(activeFrame(page).getByRole("textbox", { name: "Approval note" })).toHaveValue(
    "SECOND SESSION",
  );
});

// Two sessions on screen in turn. `show` waits for the selection to commit: the shell footer names the shown
// session's branch only once it is selected, where the rail highlights a chip before the switch lands.
async function twoSessions(page: Page, branch: string) {
  const footerBranch = page.locator(
    '.terminal-surface[data-kind="terminal:shell"] .footer-branch-name',
  );
  const firstBranch = (await footerBranch.textContent()) ?? "";
  const firstSlot = await activeSessionSlot(page);
  await createSession(page, { branch, provider: "claude" });
  const secondSlot = await waitForSessionSwitch(page, firstSlot);
  await expect(footerBranch).toHaveText(branch);
  const chip = (slot: string) => page.locator(`.session-chip[data-session-slot="${slot}"]`);
  const shown = (slot: string, name: string) => async (click: "click" | "dispatch") => {
    if (click === "click") await chip(slot).click();
    else await chip(slot).dispatchEvent("click");
    await expect(footerBranch).toHaveText(name);
  };
  return {
    firstSlot,
    showFirst: shown(firstSlot, firstBranch),
    showSecond: shown(secondSlot, branch),
  };
}

// A prompt belongs to the session it was opened for. A selection that changes while it is open (stood in for
// here by a click the modal would block) must not move the tab into the newly selected session.
test("Open URL opens in the session the prompt was opened for", async ({ page }) => {
  const url = `${origin}/form-owner`;
  const tab = page.locator(`.editor-tab[title="${url}"]`);
  const { showFirst, showSecond } = await twoSessions(page, "e2e/url-owner");
  await showFirst("click");

  await runCommand(page, "Open URL…");
  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await showSecond("dispatch");
  await input.fill(url);
  await input.press("Enter");
  await expect(input).toBeHidden();

  await showFirst("click");
  await expect(tab).toHaveCount(1);
  await showSecond("click");
  await expect(tab).toHaveCount(0);
});

// A terminal link's menu is captured with its terminal's session, so "Open in Weavie" (openUrl with a url) opens
// there even when the selection moves before the item is picked.
test("Open in Weavie on a terminal link opens in that terminal's session", async ({ page }) => {
  const url = `${origin}/form-terminal`;
  const tab = page.locator(`.editor-tab[title="${url}"]`);
  const { showFirst, showSecond } = await twoSessions(page, "e2e/url-terminal");
  await showFirst("click");

  const screen = page.locator(
    '.terminal-surface[data-kind="terminal:shell"] .term-host:not(.hidden) .xterm-screen',
  );
  await screen.click();
  await page.keyboard.type(`echo ${url}\n`);
  const cell = () =>
    page.evaluate((needle) => {
      for (const term of Object.values(window.__WEAVIE_TERMINALS__ ?? {})) {
        const buffer = term.buffer.active;
        for (let row = buffer.length - 1; row >= 0; row--) {
          const line = buffer.getLine(row)?.translateToString(true) ?? "";
          if (line.startsWith(needle)) {
            return { row: row - buffer.viewportY, col: 0, cols: term.cols, rows: term.rows };
          }
        }
      }
      return null;
    }, url);
  await expect.poll(cell).not.toBeNull();
  const at = await cell();
  const box = await screen.boundingBox();
  if (at === null || box === null) throw new Error("The echoed link is not on screen.");
  const x = box.x + (at.col + url.length / 2) * (box.width / at.cols);
  const y = box.y + (at.row + 0.5) * (box.height / at.rows);
  await page.mouse.move(x, y);
  // xterm marks a hovered link with this class in the same step that hands its URL to the menu.
  await expect(
    page.locator(
      '.terminal-surface[data-kind="terminal:shell"] .term-host:not(.hidden) .xterm-cursor-pointer',
    ),
  ).toHaveCount(1);
  await page.mouse.click(x, y, { button: "right" });
  const item = page.locator(".context-menu-item", { hasText: "Open in Weavie" });
  await expect(item).toBeVisible();

  await showSecond("dispatch");
  await item.click();
  await showFirst("click");
  await expect(tab).toHaveCount(1);
  await showSecond("click");
  await expect(tab).toHaveCount(0);
});

// A prompt opened for a session that ends (unloaded from another window here) closes and says why, rather than
// sending into the closed session.
test("Open URL closes when its session ends", async ({ page, weavie }) => {
  await twoSessions(page, "e2e/url-ended");
  await runCommand(page, "Open URL…");
  await expect(page.locator(".url-prompt-input")).toBeVisible();

  const other = await page.context().newPage();
  await other.goto(weavie.url, { waitUntil: "domcontentloaded" });
  await expect(other.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(other.locator(".session-chip.active")).toHaveAttribute("title", /^e2e\/url-ended —/);
  await runCommand(other, "Unload Session");

  await expect(page.locator(".url-prompt-input")).toBeHidden();
  await expect(
    page.locator(".toast", { hasText: "Open URL closed: its session ended." }),
  ).toBeVisible();
  await other.close();
});

test("restored inactive web tab stays dormant until activation", async ({ page, weavie }) => {
  const path = "/form-restored";
  const url = `${origin}${path}`;
  await openUrl(page, url);
  await expect(activeFrame(page).locator("#load-count")).toHaveText("Page loads: 1");
  await openFile(page, "README.md");
  await expect
    .poll(() => persistedActiveTabs(weavie.home))
    .toContain(canonicalFsPath(join(weavie.workspace, "README.md")));

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(page.locator(".editor-tab.active")).toHaveText(/README\.md/);
  await expect(page.locator(".editor-web")).toHaveCount(0);
  expect(loads.get(path)).toBe(1);

  await page.locator(".editor-tab", { hasText: new URL(url).host }).click();
  await expect(activeFrame(page).locator("#load-count")).toHaveText("Page loads: 2");
});

test("Open URL rejects a non-http(s) URL", async ({ page }) => {
  await runCommand(page, "Open URL…");

  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill("ftp://example.com");
  await input.press("Enter");

  await expect(page.locator(".url-prompt-error")).toBeVisible();
  await expect(page.locator(".editor-web")).toHaveCount(0);
});
