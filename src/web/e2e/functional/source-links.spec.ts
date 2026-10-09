import type { Page } from "@playwright/test";
import { awaitEditorReady, createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Content-link routing for connected sources (issue #992): a Notion link clicked in an agent reply opens Weavie's
// Notion viewer, not the browser, because the hello's sourceLinkHosts claims it client-side; any other link still
// leaves through openUrlExternal. The viewer's "Open in Notion" escape hatch (button + weavie.source.openInBrowser)
// hands the page back to the browser. The source connector is stubbed (WEAVIE_FAKE_NOTION, always connected).

const PAGE_URL = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d";

test.use({
  notionDoc: {
    title: "Linked Spec",
    editedTime: "2020-01-02T03:04:05.000Z",
    markdown: "Spec body.",
  },
});

// The served shell opens external links with window.open; record them instead of spawning real popups.
async function recordExternalOpens(page: Page): Promise<() => Promise<string[]>> {
  await page.evaluate(() => {
    const opened: string[] = [];
    (window as unknown as { __opened: string[] }).__opened = opened;
    window.open = (url) => {
      opened.push(String(url));
      return null;
    };
  });
  return () => page.evaluate(() => [...(window as unknown as { __opened: string[] }).__opened]);
}

test("agent-reply links: Notion opens the viewer, others the browser; Open in Notion leaves", async ({
  page,
}) => {
  await awaitEditorReady(page);
  const opened = await recordExternalOpens(page);
  await createSession(page, { branch: "notion-links", provider: "fake-acp" });
  const surface = page.locator('[data-surface="structured-agent"]');

  // The fake agent echoes the prompt back as an assistant message, so the reply carries both links as an agent
  // would print them.
  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.click();
  await composer.fill(`See ${PAGE_URL} and https://github.com/ for details.`);
  await composer.press("Enter");
  const reply = surface.locator(".agent-entry-message.agent-tone-assistant").last();
  await expect(reply).toContainText("github.com");

  // A non-Notion link still goes to the browser and opens no tab.
  await reply.locator('a[href="https://github.com/"]').click();
  await expect.poll(opened).toEqual(["https://github.com/"]);
  await expect(page.locator(".editor-source")).toHaveCount(0);

  // The Notion link opens a native source tab in Weavie, with nothing sent to the browser.
  await reply.locator(`a[href="${PAGE_URL}"]`).click();
  const source = page.locator(".editor-source");
  await expect(source.locator(".wv-title", { hasText: "Linked Spec" })).toBeVisible({
    timeout: 15_000,
  });
  await expect(page.locator(".editor-web")).toHaveCount(0);
  expect(await opened()).toEqual(["https://github.com/"]);

  // The viewer's escape hatch advertises its live keybinding and opens the page itself in the browser.
  const button = source.locator(".wv-open-external");
  await expect(button).toHaveText("Open in Notion");
  await expect(button).toHaveAttribute("title", /Alt\+Shift\+O/);
  await button.click();
  await expect.poll(opened).toEqual(["https://github.com/", PAGE_URL]);

  // The same command from the keyboard, on the active Notion tab.
  await source.locator(".wv-title").click();
  await page.keyboard.press("Alt+Shift+O");
  await expect.poll(opened).toEqual(["https://github.com/", PAGE_URL, PAGE_URL]);
});
