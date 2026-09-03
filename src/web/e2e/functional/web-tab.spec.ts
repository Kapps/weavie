import { createServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import {
  activeSessionSlot,
  createSession,
  openFile,
  runCommand,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { persistedSessions } from "../harness/persisted-sessions";

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
      <label>Approval note <input aria-label="Approval note"></label>`);
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
  await runCommand(page, "Open URL");
  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill(url);
  await input.press("Enter");
  await expect(page.locator(".editor-tab.active")).toHaveAttribute("title", url);
}

function activeFrame(page: import("@playwright/test").Page) {
  return page.frameLocator(".editor-web:not([hidden]) iframe");
}

test("web tab retains its live page state across editor tabs until close", async ({ page }) => {
  const path = "/form-state";
  const url = `${origin}${path}`;
  await openUrl(page, url);

  const frame = activeFrame(page);
  await expect(frame.locator("#load-count")).toHaveText("Page loads: 1");
  await frame.getByRole("textbox", { name: "Approval note" }).fill("APPROVE CANARY");

  await openFile(page, "README.md");
  const retained = page.locator(".editor-web");
  await expect(retained).toBeHidden();
  await expect(retained).toHaveAttribute("inert", "");
  await expect(retained).not.toHaveAttribute("tabindex", "0");
  await expect(retained.locator("iframe")).toHaveCount(1);

  await page.locator(".editor-tab", { hasText: new URL(url).host }).click();
  await expect(frame.locator("#load-count")).toHaveText("Page loads: 1");
  await expect(frame.getByRole("textbox", { name: "Approval note" })).toHaveValue("APPROVE CANARY");
  await expect(retained).not.toHaveAttribute("inert", "");
  await expect(retained).toHaveAttribute("tabindex", "0");

  await retained.focus();
  await expect(retained).toBeFocused();
  await page.keyboard.press("Control+Tab");
  await expect(page.locator(".editor-tab.active")).toHaveText(/README\.md/);
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

test("restored inactive web tab stays dormant until activation", async ({ page, weavie }) => {
  const path = "/form-restored";
  const url = `${origin}${path}`;
  await openUrl(page, url);
  await expect(activeFrame(page).locator("#load-count")).toHaveText("Page loads: 1");
  await openFile(page, "README.md");
  await expect.poll(() => persistedSessions(weavie.home)).toContain(path);

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(page.locator(".editor-tab.active")).toHaveText(/README\.md/);
  await expect(page.locator(".editor-web")).toHaveCount(0);
  expect(loads.get(path)).toBe(1);

  await page.locator(".editor-tab", { hasText: new URL(url).host }).click();
  await expect(activeFrame(page).locator("#load-count")).toHaveText("Page loads: 2");
});

test("Open URL rejects a non-http(s) URL", async ({ page }) => {
  await runCommand(page, "Open URL");

  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill("ftp://example.com");
  await input.press("Enter");

  await expect(page.locator(".url-prompt-error")).toBeVisible();
  await expect(page.locator(".editor-web")).toHaveCount(0);
});
