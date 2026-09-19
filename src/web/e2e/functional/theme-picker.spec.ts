import { openCommandPalette, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { decodeTestWebSocketMessage } from "../harness/websocket-codec";

test("theme picker previews with the keyboard, cancels, and persists acceptance", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "light" });
  const appearance = page.locator("html");
  await expect(appearance).toHaveAttribute("data-theme-type", "light");
  await openCommandPalette(page);
  await page.locator(".tb-omnibar-input").fill(">Select Color Theme");
  const command = page.locator(".tb-omnibar-row", { hasText: "Select Color Theme" });
  await expect(command.locator(".tb-row-keys")).not.toHaveText("");
  await command.click();
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  const filter = picker.getByRole("combobox", { name: "Filter themes", exact: true });
  await expect(filter).toBeFocused();
  await expect(picker.getByLabel("Theme appearance")).toHaveValue("light");
  await expect(picker.getByRole("option", { name: /Weavie Dark/ })).toHaveCount(0);
  await expect(page.locator(".modal-backdrop")).toHaveCSS("backdrop-filter", "none");
  await expect(picker.getByRole("listbox").getByRole("option", { selected: true })).toContainText(
    "Weavie Light",
  );
  await picker.getByLabel("Theme appearance").selectOption("all");
  await filter.press("ArrowUp");
  await expect(appearance).toHaveAttribute("data-theme-type", "dark");
  await filter.press("ArrowDown");
  await expect(picker.getByRole("listbox").getByRole("option", { selected: true })).toContainText(
    "Weavie Light",
  );
  await expect(appearance).toHaveAttribute("data-theme-type", "light");
  await filter.press("ArrowUp");
  await expect(appearance).toHaveAttribute("data-theme-type", "dark");
  await filter.press("Escape");
  await expect(picker).toBeHidden();
  await expect(appearance).toHaveAttribute("data-theme-type", "light");

  await runCommand(page, "Select Color Theme…");
  await filter.fill("no-theme-matches-this-query");
  await expect(picker.getByText("No themes found.")).toBeVisible();
  await expect(picker.getByRole("button", { name: "Apply", exact: true })).toBeDisabled();
  await picker.getByLabel("Theme appearance").selectOption("dark");
  await filter.fill("Weavie Dark");
  await expect(picker.getByRole("listbox").getByRole("option")).toHaveCount(1);
  await filter.press("Enter");
  await expect(picker).toBeHidden();
  await expect(appearance).toHaveAttribute("data-theme-type", "dark");
  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0);
  await expect(appearance).toHaveAttribute("data-theme-type", "dark");
  await runCommand(page, "Select Color Theme…");
  await expect(picker.getByRole("option", { name: /Weavie Dark/ })).toContainText("✓");
  await picker.getByLabel("Theme appearance").selectOption("all");
  await filter.press("ArrowDown");
  await expect(appearance).toHaveAttribute("data-theme-type", "light");
  await filter.press("Escape");
  await expect(appearance).toHaveAttribute("data-theme-type", "dark");
});

test("Enter applies the highlighted theme after navigating from a focused option", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "dark" });
  await runCommand(page, "Select Color Theme…");
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await picker.getByLabel("Theme appearance").selectOption("all");
  await picker.getByRole("option", { name: /Weavie Dark/ }).focus();
  await page.keyboard.press("ArrowDown");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  await page.keyboard.press("Enter");
  await expect(picker).toBeHidden();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
});

test.describe("pending registry search", () => {
  test.use({
    preNavigate: {
      run: async (page) => {
        await page.routeWebSocket("**/*", (socket) => {
          const server = socket.connectToServer();
          socket.onMessage((data) => {
            const message = JSON.parse(decodeTestWebSocketMessage(data));
            if (message.feature === "themes" && message.name === "search") return;
            server.send(data);
          });
        });
      },
    },
  });

  test("switching to installed themes releases registry loading state", async ({ page }) => {
    await runCommand(page, "Select Color Theme…");
    const picker = page.getByRole("dialog", { name: "Select Color Theme" });
    await picker.getByRole("button", { name: "Open VSX", exact: true }).click();
    await expect(picker.getByRole("status")).toHaveText(
      "Loading themes… Temporary connection failures are retried automatically.",
    );
    await picker.getByRole("button", { name: "Installed", exact: true }).click();
    await expect(picker.getByRole("status")).toHaveCount(0);
    await expect(picker.getByRole("button", { name: "Apply", exact: true })).toBeEnabled();
    await picker.getByLabel("Theme appearance").selectOption("light");
    await picker.getByRole("combobox", { name: "Filter themes", exact: true }).fill("Weavie Light");
    await picker.getByRole("combobox", { name: "Filter themes", exact: true }).press("Enter");
    await expect(picker).toBeHidden();
    await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  });
});
