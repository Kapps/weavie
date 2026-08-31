import { expect, test } from "../harness/fixtures";

test("application menus expose command shortcuts, submenus, and dispatch", async ({ page }) => {
  const modifier = process.platform === "darwin" ? "⌘" : "Ctrl";
  const file = page.getByRole("menuitem", { name: "File", exact: true });
  await file.click();
  await file.click();
  await expect(file).toHaveAttribute("aria-expanded", "false");
  await expect(page.getByRole("menu")).toBeHidden();

  for (const label of ["File", "Go", "View", "Run"]) {
    const topLevel = page.getByRole("menuitem", { name: label, exact: true });
    await topLevel.click();
    await expect(topLevel).toHaveAttribute("aria-expanded", "true");
    await expect(page.getByRole("menu")).toBeVisible();
    if (label === "Go") {
      await expect(page.getByRole("menuitem", { name: /^Go Back/ })).toBeDisabled();
      await expect(page.getByRole("menuitem", { name: /^Next Session/ })).toBeDisabled();
    }
  }

  const diff = page.getByRole("menuitem", { name: "Diff", exact: true });
  await expect(diff).toBeVisible();
  await diff.click();
  await expect(page.getByRole("menuitem", { name: "Review Changes", exact: true })).toBeDisabled();

  const diffAgainst = page.getByRole("menuitem", { name: /^Diff Against…/ });
  await expect(diffAgainst).toBeVisible();
  await expect(diffAgainst.locator(".context-menu-keys")).toHaveText(`${modifier}+Shift+D`);
  await page.getByRole("menuitem", { name: "Diff Against HEAD", exact: true }).click();
  await expect(page.locator(".toast", { hasText: "No changes against 'HEAD'" })).toBeVisible({
    timeout: 30_000,
  });

  await page.getByRole("menuitem", { name: "View", exact: true }).click();
  await page.getByRole("menuitem", { name: "Appearance", exact: true }).click();
  const increase = page.getByRole("menuitem", { name: /^Increase Font Size/ });
  await expect(increase).toBeVisible();
  await expect(increase.locator(".context-menu-keys")).toContainText(`${modifier}+`);
});

test("application menu supports keyboard traversal across the top level", async ({ page }) => {
  const file = page.getByRole("menuitem", { name: "File", exact: true });
  await file.focus();
  await file.press("ArrowRight");
  await expect(page.getByRole("menuitem", { name: "Go", exact: true })).toBeFocused();

  await page.keyboard.press("ArrowDown");
  await expect(page.getByRole("menu")).toBeVisible();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("menuitem", { name: "View", exact: true })).toHaveAttribute(
    "aria-expanded",
    "true",
  );
});
