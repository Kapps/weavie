import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import type { ThemePreview } from "../../src/theme/picker-state";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const packages = Array.from({ length: 16 }, (_, index) => ({
  namespace: "example-author",
  name: `ocean-${index}`,
  displayName: `Ocean ${index}`,
  description: "Ocean color themes",
  version: "1.0.0",
  downloadCount: 16000 - index,
}));
let pending: Map<string, () => void>;
let searches: number;

function variants(name: string): ThemePreview[] {
  return (["dark", "light"] as const).map((type) => ({
    choice: {
      id: `${name}-${type}`,
      label: `${name} ${type}`,
      type,
      namespace: "example-author",
      name,
      version: "1.0.0",
    },
    slot: { id: `weavie-${type}` },
  }));
}

test.use({
  preNavigate: {
    run: async (page) => {
      pending = new Map();
      searches = 0;
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        socket.onMessage((data) => {
          const message = JSON.parse(data.toString()) as MessageEnvelope;
          const reply = (payload: unknown) =>
            socket.send(JSON.stringify({ ...message, kind: "response", payload }));
          if (message.kind === "request" && message.feature === "themes") {
            if (message.name === "search") {
              searches++;
              const { offset } = message.payload as { offset: number };
              reply({ extensions: packages.slice(offset, offset + 8), offset, totalSize: 16 });
              return;
            }
            if (message.name === "previewExtension") {
              const { name } = message.payload as { name: string };
              pending.set(name, () => reply(variants(name)));
              return;
            }
          }
          server.send(data);
        });
      });
    },
  },
});

async function release(name: string) {
  await expect.poll(() => pending.has(name)).toBe(true);
  const respond = pending.get(name);
  if (respond === undefined) throw new Error(`Missing preview request: ${name}`);
  respond();
  pending.delete(name);
}

test("package variants preserve registry position and support hover and keyboard previews", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "light" });
  await runCommand(page, "Select Color Theme…");
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await picker.getByRole("button", { name: "Open VSX", exact: true }).click();
  const search = picker.getByRole("combobox", { name: "Search Open VSX themes" });
  const sort = picker.getByRole("combobox", { name: "Sort Open VSX themes" });
  await sort.selectOption("relevance");
  await search.fill("ocean");
  await picker.getByRole("button", { name: "Load more (8 of 16)" }).click();
  const results = picker.getByRole("listbox", { name: "Color themes", exact: true });
  await expect(results.getByRole("option")).toHaveCount(16);
  const selected = results.getByRole("option", { name: /^Ocean 12 / });
  await selected.scrollIntoViewIfNeeded();
  const scroll = await results.evaluate((element) => element.scrollTop);
  expect(scroll).toBeGreaterThan(0);
  const before = searches;
  await selected.click();
  await release("ocean-12");
  const filter = picker.getByRole("combobox", { name: "Filter theme variants" });
  await expect(filter).toBeFocused();
  const choices = picker.getByRole("listbox", { name: "Theme variants", exact: true });
  const appearance = picker.getByLabel("Theme appearance");
  await expect(appearance).toHaveValue("light");
  await expect(choices.getByRole("option")).toHaveCount(1);
  await expect(choices).toContainText("ocean-12 light");
  await appearance.selectOption("dark");
  await expect(choices.getByRole("option")).toHaveCount(1);
  await expect(choices).toContainText("ocean-12 dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "dark");
  await appearance.selectOption("all");
  await expect(choices.getByRole("option")).toHaveCount(2);
  await choices.getByRole("option", { name: /ocean-12 light/ }).hover();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  await choices.getByRole("option", { name: /ocean-12 dark/ }).hover();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "dark");
  await expect(search).toHaveValue("ocean");
  await expect(sort).toHaveValue("relevance");
  await expect(results.getByRole("option")).toHaveCount(16);
  expect(await results.evaluate((element) => element.scrollTop)).toBe(scroll);
  expect(searches).toBe(before);

  await filter.press("ArrowDown");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  await choices.getByRole("option", { name: /ocean-12 light/ }).focus();
  await page.keyboard.press("ArrowLeft");
  await expect(selected).toBeFocused();
  await page.keyboard.press("ArrowUp");
  await page.keyboard.press("ArrowRight");
  await release("ocean-11");
  await expect(filter).toBeFocused();
  await expect(choices).toContainText("ocean-11 dark");
  await filter.press("Escape");
  await expect(picker).toBeHidden();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
});

test("obsolete package responses cannot replace another package or reopen a dismissed pane", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "light" });
  await runCommand(page, "Select Color Theme…");
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await picker.getByRole("button", { name: "Open VSX", exact: true }).click();
  const results = picker.getByRole("listbox", { name: "Color themes", exact: true });
  await results.getByRole("option").nth(0).click();
  await expect.poll(() => pending.has("ocean-0")).toBe(true);
  await results.getByRole("option").nth(1).click();
  await release("ocean-1");
  const choices = picker.getByRole("listbox", { name: "Theme variants", exact: true });
  await picker.getByLabel("Theme appearance").selectOption("all");
  await expect(choices).toContainText("ocean-1 dark");
  await release("ocean-0");
  await choices.getByRole("option", { name: /ocean-1 light/ }).hover();
  await expect(choices).not.toContainText("ocean-0");
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");

  await results.getByRole("option").nth(2).click();
  await expect.poll(() => pending.has("ocean-2")).toBe(true);
  await picker.getByRole("combobox", { name: "Search Open VSX themes" }).fill("new query");
  await release("ocean-2");
  await expect(results.getByRole("option")).toHaveCount(8);
  await expect(choices).toHaveCount(0);
  await results.getByRole("option").nth(3).click();
  await expect.poll(() => pending.has("ocean-3")).toBe(true);
  await picker.getByRole("button", { name: "Installed", exact: true }).click();
  await release("ocean-3");
  await expect(picker.getByRole("option", { name: /Weavie Light/ })).toBeVisible();
  await expect(choices).toHaveCount(0);
  await expect(picker.getByRole("status")).toHaveCount(0);
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
});

test("a completed package keeps focus in results when the user has moved on", async ({ page }) => {
  await runCommand(page, "Select Color Theme…");
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await picker.getByRole("button", { name: "Open VSX", exact: true }).click();
  const results = picker.getByRole("listbox", { name: "Color themes", exact: true });
  await results.getByRole("option").first().click();
  await expect.poll(() => pending.has("ocean-0")).toBe(true);
  const search = picker.getByRole("combobox", { name: "Search Open VSX themes" });
  await search.focus();
  await release("ocean-0");
  const filter = picker.getByRole("combobox", { name: "Filter theme variants" });
  await expect(filter).toBeVisible();
  await expect(search).toBeFocused();
  await filter.fill("ocean");
  await filter.press("ArrowLeft");
  await expect(filter).toBeFocused();
  await filter.fill("");
  await filter.press("ArrowLeft");
  await expect(results.getByRole("option").first()).toBeFocused();
  await picker.getByRole("button", { name: "Close variants" }).click();
  await expect(filter).toHaveCount(0);
  await expect(results.getByRole("option").first()).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect.poll(() => pending.has("ocean-0")).toBe(true);
  await page.keyboard.press("ArrowDown");
  await release("ocean-0");
  await expect(filter).toBeVisible();
  await expect(results.getByRole("option").first()).toBeFocused();
  await expect(results.getByRole("option").nth(1)).toHaveAttribute("aria-selected", "true");
});
