import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { awaitEditorReady, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("file browser uses omnibar fuzzy ranking and bounds results without losing large-repo files", async ({
  page,
  weavie,
}) => {
  const fileCount = 26_000;
  for (let bucket = 0; bucket < 100; bucket += 1) {
    const directory = join(weavie.workspace, "generated", `bucket-${bucket}`);
    mkdirSync(directory, { recursive: true });
    for (let offset = 0; offset < 260; offset += 1) {
      const index = String(bucket * 260 + offset).padStart(5, "0");
      writeFileSync(join(directory, `QueryFile-${index}.txt`), `File ${index}\n`);
    }
  }
  mkdirSync(join(weavie.workspace, "retained"));
  for (const name of ["FileBrowserFilter.txt", "FileBrowserFrame.txt", "FastBufferFactory.txt"]) {
    writeFileSync(join(weavie.workspace, "retained", name), `${name}\n`);
  }
  await awaitEditorReady(page);
  const omnibar = page.locator(".tb-omnibar-input");
  await omnibar.click();
  await omnibar.fill("q");
  await expect(page.locator(".tb-omnibar-row")).toHaveCount(300);
  await expect(page.locator(".tb-omnibar-more")).toContainText(`+${fileCount - 300} more`);
  await omnibar.fill("fbf");
  await expect(page.locator(".tb-omnibar-row")).toHaveCount(3);
  const omnibarOrder = await page.locator(".tb-omnibar-row .tb-row-leaf").allTextContents();
  await omnibar.press("Escape");

  await runCommand(page, "Toggle File Browser");
  await page.locator(".browser-row", { hasText: "retained" }).click();
  const retained = page.locator(".browser-row:not(.browser-filter-result)", {
    hasText: "FileBrowserFilter.txt",
  });
  await expect(retained).toBeVisible();
  await page.getByRole("button", { name: "Filter", exact: true }).click();
  const input = page.getByRole("combobox", { name: "Filter files by name or path" });
  const results = page.locator(".browser-filter-result");
  await expect(input).toBeFocused();
  await input.fill("fbf");
  await expect(results).toHaveCount(3);
  await expect(results.locator(".browser-name")).toHaveText(omnibarOrder);

  await input.fill("q");
  await expect(results).toHaveCount(300);
  await expect(page.locator(".browser-filter-count")).toHaveText(`${fileCount} matches`);
  await expect(page.locator(".browser-filter-more")).toContainText(`+${fileCount - 300} more`);
  await expect(page.locator(".browser-filter-more")).toBeInViewport();
  await expect(results.filter({ hasText: "QueryFile-25999.txt" })).toHaveCount(0);

  await input.fill("QueryFile-25999");
  await expect(results).toHaveCount(1);
  await expect(results).toContainText("QueryFile-25999.txt");
  await expect(page.locator(".browser-filter-count")).toHaveText("1 matches");
  await expect(page.locator(".browser-filter-more")).toHaveCount(0);
  await input.press("Enter");
  await expect(page.locator(".editor")).toHaveAttribute(
    "data-active-file",
    /[\\/]QueryFile-25999\.txt$/,
  );
  await input.press("Escape");
  await expect(input).toHaveCount(0);
  await expect(retained).toBeVisible();
});
