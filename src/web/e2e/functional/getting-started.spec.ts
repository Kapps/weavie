import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { decodeTestWebSocketMessage, encodeTestWebSocketMessage } from "../harness/websocket-codec";

test.use({
  setupCompleted: false,
  // Open VSX is a live service; answer theme searches with one canned extension.
  preNavigate: {
    run: async (page) => {
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        socket.onMessage((data) => {
          const message = JSON.parse(decodeTestWebSocketMessage(data)) as MessageEnvelope;
          if (
            message.kind !== "request" ||
            message.feature !== "themes" ||
            message.name !== "search"
          ) {
            server.send(data);
            return;
          }
          const extension = {
            namespace: "example-author",
            name: "popular",
            displayName: "Popular Theme",
            version: "1.2.3",
            description: "A popular theme.",
            downloadCount: 12345,
          };
          const payload = { extensions: [extension], offset: 0, totalSize: 1 };
          socket.send(
            encodeTestWebSocketMessage(JSON.stringify({ ...message, kind: "response", payload })),
          );
        });
      });
    },
  },
});

test("first run opens Getting Started, saves each choice live, and stays closed once finished", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "dark" });
  const setup = page.locator(".getting-started-dialog");
  const heading = setup.getByRole("heading", { level: 2 });
  await expect(heading).toHaveText("Choose a look");

  await setup.getByRole("button", { name: "Light", exact: true }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  await expect(setup.getByRole("button", { name: "Light", exact: true })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await setup.getByRole("button", { name: "Next" }).click();

  await expect(heading).toHaveText("Pick your agent");
  await expect(setup.getByRole("button", { name: /Claude Code/ })).toBeEnabled();
  const fakeAcp = setup.getByRole("button", { name: /Fake ACP/ });
  await fakeAcp.click();
  await expect(fakeAcp).toHaveAttribute("aria-pressed", "true");
  await setup.getByRole("button", { name: "Next" }).click();

  await expect(heading).toHaveText("Smart suggestions");
  const inference = setup.getByRole("switch", { name: /Allow suggestions/ });
  await expect(inference).toBeEnabled();
  await inference.check();
  await expect(setup.getByRole("combobox", { name: /Answered by/ })).toHaveValue("fake-acp");
  await expect(setup.getByRole("switch", { name: /Suggest automatically/ })).toBeEnabled();
  await setup.getByRole("button", { name: "Next" }).click();

  await expect(heading).toHaveText("Learn the keys");
  await expect(
    setup.locator(".gs-keys li", { hasText: "Sessions" }).locator("kbd").last(),
  ).toHaveText("N");
  await expect(setup.locator(".gs-keys kbd", { hasText: "Unbound" })).toHaveCount(0);
  await page.keyboard.press("Enter");
  await expect(setup).toBeHidden();

  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  await expect(setup).toHaveCount(0);
  await expect(
    page.locator(".toast", { hasText: "Let Weavie use automatic inference" }),
  ).toHaveCount(0);

  await runCommand(page, "Getting Started");
  await expect(heading).toHaveText("Choose a look");
  await page.keyboard.press("Escape");
  await expect(setup).toBeHidden();
});

test("Browse more themes hands setup off to the Open VSX picker and back", async ({ page }) => {
  const setup = page.locator(".getting-started-dialog");
  await expect(setup.getByRole("heading", { level: 2 })).toHaveText("Choose a look");
  await expect(setup.locator(".gs-themes")).toContainText("Using Weavie Light and Weavie Dark.");

  await setup.getByRole("button", { name: /Browse more themes/ }).click();
  const picker = page.getByRole("dialog", { name: "Select Color Theme" });
  await expect(picker.getByRole("button", { name: "Open VSX", exact: true })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(picker.getByRole("option", { name: /Popular Theme/ })).toBeVisible();
  await expect(setup).toHaveCount(0);

  await page.keyboard.press("Escape");
  await expect(picker).toBeHidden();
  await expect(setup.getByRole("heading", { level: 2 })).toHaveText("Choose a look");
});
