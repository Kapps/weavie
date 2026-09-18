import { mkdir, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openCommandPalette, openFile, openSearch, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("Enter activates the focused dialog action and leaves IME composition alone", async ({
  page,
}) => {
  await runCommand(page, "Open URL…");
  const dialog = page.getByRole("dialog", { name: "Open URL" });
  const input = dialog.locator(".url-prompt-input");
  await input.fill("http://localhost:3000");
  await input.dispatchEvent("keydown", { key: "Enter", isComposing: true });
  await expect(dialog).toBeVisible();
  await input.dispatchEvent("keydown", { key: "Escape", isComposing: true });
  await expect(dialog).toBeVisible();
  await input.press("Tab");
  await expect(dialog.getByRole("button", { name: "Cancel", exact: true })).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(dialog).toHaveCount(0);
  await expect(page.locator(".editor-web")).toHaveCount(0);

  await runCommand(page, "Open URL…");
  await input.fill("http://localhost:3000");
  await input.press("Enter");
  await expect(dialog).toHaveCount(0);
  await expect(page.locator(".editor-tab.active")).toHaveAttribute(
    "title",
    "http://localhost:3000/",
  );
});

test("search buttons keep native keyboard activation and right-click does not open results", async ({
  page,
}) => {
  await openFile(page, "README.md");
  await openSearch(page);
  const input = page.locator(".search-input");
  await input.fill("greet");
  const rows = page.locator(".search-row");
  await expect(rows).toHaveCount(2);
  await input.press("Tab");
  const matchCase = page.locator(".search-toggle").first();
  await expect(matchCase).toBeFocused();
  await page.keyboard.press("Enter");
  await expect(matchCase).toHaveAttribute("aria-pressed", "true");
  await expect(input).toBeFocused();
  await expect(rows).toHaveCount(2);
  await rows.first().click({ button: "right" });
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /README\.md$/);
  await page.keyboard.press("Escape");
  await openSearch(page);
  await rows.first().click();
  await expect(page.locator(".editor")).toHaveAttribute("data-active-file", /hello\.ts$/);
});

test("palette and file tree leave secondary clicks and composition keys alone", async ({
  page,
  weavie,
}) => {
  await mkdir(join(weavie.workspace, "ime-folder"));
  await writeFile(join(weavie.workspace, "ime-folder/child.txt"), "composition test");
  await openCommandPalette(page);
  const input = page.locator(".tb-omnibar-input");
  await input.fill(">Open URL");
  const row = page.locator(".tb-omnibar-row", { hasText: "Open URL…" });
  await expect(row).toBeVisible();
  for (const key of ["Enter", "Escape"]) {
    await input.dispatchEvent("keydown", { key, isComposing: true });
    await expect(row).toBeVisible();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  }
  for (const button of ["right", "middle"] as const) {
    await row.click({ button });
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(row).toBeVisible();
  }
  await row.click();
  await expect(page.getByRole("dialog", { name: "Open URL" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
  await input.click();
  await input.fill("");
  const directory = page.locator(".tb-tree-row.dir", { hasText: "ime-folder" });
  const child = page.locator(".tb-tree-row", { hasText: "child.txt" });
  await directory.hover();
  await expect(directory).toHaveClass(/selected/);
  await input.dispatchEvent("keydown", { key: "ArrowRight", isComposing: true });
  await expect(child).toHaveCount(0);
  await input.press("ArrowRight");
  await expect(child).toBeVisible();
  await input.dispatchEvent("keydown", { key: "ArrowLeft", isComposing: true });
  await expect(child).toBeVisible();
  await input.press("ArrowLeft");
  await expect(child).toHaveCount(0);
});
