import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Open URL (the weavie.workspace.openUrl command / $mod+O) → a prompt → an http(s) URL opens as a web tab that
// renders an iframe over the editor. The overlay is pure web (no host round-trip), so this is headless-only and
// asserts the iframe's `src` + the tab chrome — not the framed page's loaded content (no server is required).

test("Open URL opens an http(s) page in a web tab", async ({ page }) => {
  await runCommand(page, "Open URL");

  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill("http://localhost:8099");
  await input.press("Enter");

  // A web tab appears in the strip: a globe icon + the URL host as its label.
  const tab = page.locator(".editor-tab", { hasText: "localhost:8099" });
  await expect(tab).toBeVisible();
  await expect(tab.locator(".editor-tab-icon")).toBeVisible();

  // The web surface overlays the editor with an iframe pointed at the normalized URL.
  const frame = page.locator(".editor-web iframe");
  await expect(frame).toBeVisible();
  await expect(frame).toHaveAttribute("src", "http://localhost:8099/");

  // Closing the tab tears the iframe down.
  await tab.locator(".editor-tab-close").click();
  await expect(page.locator(".editor-web")).toHaveCount(0);
});

test("Open URL rejects a non-http(s) URL", async ({ page }) => {
  await runCommand(page, "Open URL");

  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill("ftp://example.com");
  await input.press("Enter");

  // The prompt stays open with an error, and no web tab is created.
  await expect(page.locator(".url-prompt-error")).toBeVisible();
  await expect(page.locator(".editor-web")).toHaveCount(0);
});
