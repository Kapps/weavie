import { awaitEditorReady, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("a chunk that fails to download offers a reload that recovers it", async ({ page }) => {
  await awaitEditorReady(page);
  const chunk = "**/assets/FileBrowser-*.js";
  await page.route(chunk, (route) => route.abort("internetdisconnected"));
  await runCommand(page, "Toggle File Browser");
  const toast = page.getByRole("alert").filter({ hasText: "Part of Weavie failed to download" });
  await expect(toast).toBeVisible();

  await page.unroute(chunk);
  await toast.getByRole("button", { name: "Reload" }).click();
  await awaitEditorReady(page);
  await expect(toast).toHaveCount(0);
  await runCommand(page, "Toggle File Browser");
  await expect(page.locator(".browser-panel")).toBeVisible();
});
