import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Source-URL routing through the HOST: the web sends every opened URL as `open-target`; the host checks each
// registered source's ISource.Match and either fetches it (a notion.so/notion.site URL → `source-doc`, a
// native rich-HTML source tab) or bounces it back as `open-web` (an iframe web tab). The match lives host-side,
// so this guards OUR routing — not the model. The source connector is stubbed (WEAVIE_FAKE_NOTION) with the doc
// below, whose `html` the SourceView renders verbatim into its shadow root.

const NOTION_DOC = {
  title: "Source Routing Doc",
  text: "# Source Routing Doc",
  html: '<h1>Source Routing Doc</h1><p>Fetched + rendered natively.</p><pre><code class="language-typescript">const x: number = 1;</code></pre>',
};

test.use({ notionDoc: NOTION_DOC });

// Open the URL prompt (the weavie.workspace.openUrl command) and submit `url`.
async function openUrl(page: import("@playwright/test").Page, url: string): Promise<void> {
  await runCommand(page, "Open URL");
  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill(url);
  await input.press("Enter");
}

test("a Notion URL routes through the host to a native source tab (not an iframe)", async ({
  page,
}) => {
  await openUrl(page, "https://www.notion.so/Source-Routing-Doc-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

  // The host matched it, fetched the doc, and pushed `source-doc`: a SOURCE tab labelled with the doc title.
  const sourceTab = page.locator(".editor-tab", { hasText: "Source Routing Doc" });
  await expect(sourceTab).toBeVisible({ timeout: 15_000 });

  // The SourceView shadow root renders the fetched rich HTML (heading + a highlighted code block) — proving
  // the native render, not a blank iframe. Playwright pierces the open shadow root.
  const source = page.locator(".editor-source");
  await expect(source).toBeVisible();
  await expect(source.locator("h1", { hasText: "Source Routing Doc" })).toBeVisible();

  // The bug this routing replaced would have produced a blank `.editor-web` iframe of notion.so; assert none.
  await expect(page.locator(".editor-web")).toHaveCount(0);
});

test("a non-Notion URL routes through the host to a web (iframe) tab", async ({ page }) => {
  await openUrl(page, "https://example.com");

  // The host didn't match it and replied `open-web`: a globe tab + an iframe pointed at the URL.
  await expect(page.locator(".editor-tab", { hasText: "example.com" })).toBeVisible({
    timeout: 15_000,
  });
  const frame = page.locator(".editor-web iframe");
  await expect(frame).toBeVisible();
  await expect(frame).toHaveAttribute("src", "https://example.com/");

  // It is NOT a source tab — the host's Match correctly declined it.
  await expect(page.locator(".editor-source")).toHaveCount(0);
});
