import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Source-URL routing through the HOST: the web sends every opened URL as `open-target`; the host checks each
// registered source's ISource.Match and either fetches it (a notion.so/notion.site URL → `source-doc`, a native
// source tab) or bounces it back as `open-web` (an iframe web tab). The match lives host-side, so this guards OUR
// routing — not the model. The source connector is stubbed (WEAVIE_FAKE_NOTION) with the enhanced-markdown below,
// which the SourceView renders (renderNotionMarkdown → shadow root) — also exercising the markdown render path.

const NOTION_DOC = {
  title: "Source Routing Doc",
  editedTime: "2020-01-02T03:04:05.000Z",
  // Notion enhanced markdown AS THE API RETURNS IT: one block per line, single-\n separated (no blank lines),
  // tab-indented container children — so this exercises renderNotionMarkdown's normalizer, not just markdown-it.
  markdown: [
    'Fetched + rendered natively, with a <span color="red">red</span> word.',
    '<callout icon="💡" color="blue_bg">',
    "\tA callout body.",
    "</callout>",
    "<details>",
    "\t<summary>Toggle title</summary>",
    "\tHidden toggle body.",
    "</details>",
    '## Section {toggle="true"}',
    "\tInside the toggle heading.",
    "```typescript",
    "const x: number = 1;",
    "```",
  ].join("\n"),
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

  // The SourceView shadow root renders the page header (title + last-edited) over the fetched content — proving
  // the native render, not a blank iframe. Playwright pierces the open shadow root.
  const source = page.locator(".editor-source");
  await expect(source).toBeVisible();
  await expect(source.locator(".wv-title", { hasText: "Source Routing Doc" })).toBeVisible();
  await expect(source.locator(".wv-meta")).toContainText("Edited");

  // The bug this routing replaced would have produced a blank `.editor-web` iframe of notion.so; assert none.
  await expect(page.locator(".editor-web")).toHaveCount(0);
});

test("enhanced markdown renders (callout + color) and the toggle expands on click", async ({
  page,
}) => {
  await openUrl(page, "https://www.notion.so/Source-Routing-Doc-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");
  const source = page.locator(".editor-source");
  await expect(source.locator(".wv-title", { hasText: "Source Routing Doc" })).toBeVisible({
    timeout: 15_000,
  });

  // Notion's HTML extensions are mapped onto our stylesheet classes: <callout> → .wv-callout, <span color> →
  // .wv-color-red — proving renderNotionMarkdown's transform, not just markdown-it.
  await expect(source.locator(".wv-callout", { hasText: "A callout body" })).toBeVisible();
  await expect(source.locator(".wv-color-red")).toHaveText("red");

  // A <details> toggle block and a toggle HEADING both start collapsed and reveal their body when the summary is
  // clicked — proving the SourceView click handler drives <details> open/closed (the WebView doesn't natively).
  const toggleBody = source.locator("details p", { hasText: "Hidden toggle body" });
  await expect(toggleBody).toBeHidden();
  await source.locator("summary", { hasText: "Toggle title" }).click();
  await expect(toggleBody).toBeVisible();

  const headingBody = source.locator(".wv-toggle-heading p", {
    hasText: "Inside the toggle heading",
  });
  await expect(headingBody).toBeHidden();
  await source.locator("summary", { hasText: "Section" }).click();
  await expect(headingBody).toBeVisible();
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
