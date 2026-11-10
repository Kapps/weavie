import { awaitEditorReady, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ automaticInference: true });

test("an empty workspace offers files once and preserves restored tabs", async ({ page }) => {
  const search = page.getByRole("combobox", { name: "Go to file", exact: true });
  const omnibar = page.locator(".tb-omnibar-box");
  await expect(search).toBeFocused();
  await expect(page.locator(".tb-omnibar-row", { hasText: "hello.ts" })).toBeVisible();

  await search.press("Escape");
  await expect(omnibar).not.toHaveClass(/\bopen\b/);
  await awaitEditorReady(page);
  await expect(omnibar).not.toHaveClass(/\bopen\b/);

  await openFile(page, "hello.ts");
  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0);
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /[/\\]hello\.ts$/);
  await expect(omnibar).not.toHaveClass(/\bopen\b/);
  await expect(search).not.toBeFocused();

  await runCommand(page, "Close All Editors");
  await expect(page.locator(".editor-tab")).toHaveCount(0);
  await expect(omnibar).not.toHaveClass(/\bopen\b/);
});
