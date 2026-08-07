import { mkdir, rmdir } from "node:fs/promises";
import { join } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("an expanded empty directory settles as empty instead of loading forever", async ({
  page,
  weavie,
}) => {
  await mkdir(join(weavie.workspace, "empty-folder"));

  await runCommand(page, "Toggle File Browser");
  await page.locator(".browser-row", { hasText: "empty-folder" }).click();

  await expect(page.locator(".browser-children .browser-empty")).toHaveText("Empty folder");
  await expect(page.locator(".browser-children .browser-loading")).toHaveCount(0);
});

test("a directory failure is distinct from empty and can be retried", async ({ page, weavie }) => {
  const directory = join(weavie.workspace, "temporarily-missing");
  await mkdir(directory);
  await runCommand(page, "Toggle File Browser");
  const row = page.locator(".browser-row", { hasText: "temporarily-missing" });
  await expect(row).toBeVisible();

  await rmdir(directory);
  await row.click();
  const error = page.locator(".browser-error");
  await expect(error).toContainText("Directory not found");

  await mkdir(directory);
  await error.getByRole("button", { name: "Retry" }).click();
  await expect(page.locator(".browser-children .browser-empty")).toHaveText("Empty folder");
  await expect(error).toHaveCount(0);
});
