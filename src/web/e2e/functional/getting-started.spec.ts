import type { Page } from "@playwright/test";
import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { decodeTestWebSocketMessage, encodeTestWebSocketMessage } from "../harness/websocket-codec";

const installResults = new WeakMap<Page, PromiseWithResolvers<void>>();

test.use({
  setupCompleted: false,
  // Open VSX and the ACP registry are live services; answer them with canned entries.
  preNavigate: {
    run: async (page) => {
      const installResult = Promise.withResolvers<void>();
      installResults.set(page, installResult);
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        socket.onMessage(async (data) => {
          const message = JSON.parse(decodeTestWebSocketMessage(data)) as MessageEnvelope;
          const payload = message.kind === "request" ? canned(message) : undefined;
          if (payload === undefined) {
            server.send(data);
            return;
          }
          const error = registryOffline && message.name === "list" ? OFFLINE : null;
          socket.send(
            encodeTestWebSocketMessage(
              JSON.stringify({ ...message, kind: "response", payload, error }),
            ),
          );
          // An install answers at once and reports its check later; this one fails the way npm would.
          if (message.feature === "acpRegistry" && message.name === "install") {
            const { id, operation } = message.payload as { id: string; operation: string };
            const result = {
              id,
              operation,
              error: "npm ERR! 404 Not Found - GET https://registry.npmjs.org/codex-acp",
            };
            await installResult.promise;
            socket.send(
              encodeTestWebSocketMessage(
                JSON.stringify({
                  ...message,
                  kind: "event",
                  requestId: null,
                  name: "installed",
                  payload: result,
                }),
              ),
            );
          }
        });
      });
    },
  },
});

// Set by a test to make the ACP registry list fail the way an offline machine does.
let registryOffline = false;
const OFFLINE = "getaddrinfo ENOTFOUND cdn.agentclientprotocol.com";
test.beforeEach(() => {
  registryOffline = false;
});

function canned(message: MessageEnvelope): unknown {
  if (message.feature === "themes" && message.name === "search") {
    const extension = {
      namespace: "example-author",
      name: "popular",
      displayName: "Popular Theme",
      version: "1.2.3",
      description: "A popular theme.",
      downloadCount: 12345,
    };
    return { extensions: [extension], offset: 0, totalSize: 1 };
  }
  if (message.feature === "acpRegistry" && message.name === "install") {
    return null;
  }
  if (message.feature === "acpRegistry" && message.name === "list") {
    const agent = (id: string, name: string, description: string) => ({
      id,
      name,
      version: "1.0.0",
      description,
      distributions: ["npx"],
      installedDistribution: null,
      installedVersion: null,
      broken: null,
    });
    return [
      agent("claude-acp", "Claude Agent", "ACP wrapper for Anthropic's Claude"),
      agent("codex-acp", "Codex", "ACP adapter for OpenAI's coding assistant"),
      agent("other-acp", "Other Agent", "Not a suggested agent"),
    ];
  }
  return undefined;
}

test("first run opens Getting Started, saves each choice live, and stays closed once finished", async ({
  page,
}) => {
  await page.emulateMedia({ colorScheme: "dark" });
  const setup = page.locator(".getting-started-dialog");
  const heading = setup.getByRole("heading", { level: 2 });
  await expect(heading).toHaveText("Choose a look");
  await expect(setup.locator(".gs-later")).toContainText(
    "Change it anytime with Select Color Theme",
  );

  await setup.getByRole("button", { name: "Light", exact: true }).click();
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  await expect(setup.getByRole("button", { name: "Light", exact: true })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await setup.getByRole("button", { name: "Next" }).click();

  await expect(heading).toHaveText("Pick your agent");
  await expect(setup.getByRole("button", { name: /Claude Code/ })).toBeEnabled();
  await expect(setup.getByRole("button", { name: /Claude Code/ })).toContainText(
    "Runs in a terminal inside Weavie",
  );
  for (const name of [/Codex/, /Claude Agent/]) {
    const suggested = setup.getByRole("button", { name });
    await expect(suggested).toContainText("Not installed yet. Choosing it installs it.");
    await expect(suggested).toContainText("Uses Weavie's own interface instead of a terminal");
    await expect(suggested.locator(".gs-tag")).toHaveAttribute("title", /Made by a third party/);
  }
  await expect(setup.getByRole("button", { name: /Other Agent/ })).toHaveCount(0);
  await expect(setup.getByRole("button", { name: /Claude Code/ }).locator(".gs-tag")).toHaveCount(
    0,
  );
  const fakeAcp = setup.getByRole("button", { name: /Fake ACP/ });
  await fakeAcp.click();
  await expect(fakeAcp).toHaveAttribute("aria-pressed", "true");
  await setup.getByRole("button", { name: "Next" }).click();

  await expect(heading).toHaveText("Smart suggestions");
  const inference = setup.getByRole("switch", { name: /Allow suggestions/ });
  const automatic = setup.getByRole("switch", { name: /Suggest automatically/ });
  await expect(inference).toBeChecked();
  await expect(automatic).toBeChecked();
  const suggestionsAgent = setup.getByRole("combobox", { name: /Which agent/ });
  await expect(suggestionsAgent).toHaveValue("fake-acp");
  await automatic.uncheck();
  await suggestionsAgent.selectOption("claude");
  await setup.getByRole("button", { name: "Back" }).click();
  await setup.getByRole("button", { name: "Next" }).click();
  await expect(inference).toBeChecked();
  await expect(automatic).not.toBeChecked();
  await expect(suggestionsAgent).toHaveValue("claude");
  await automatic.check();
  await setup.getByRole("button", { name: "Next" }).click();

  await expect(heading).toHaveText("You're ready");
  await expect(setup.locator(".gs-ask")).toContainText("Just ask your agent.");
  await expect(setup.locator(".gs-keys li", { hasText: "Go to File" })).toContainText(
    "orShiftShift",
  );
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

test("choosing a suggested agent installs and checks it, and shows why a failed install failed", async ({
  page,
}) => {
  const setup = page.locator(".getting-started-dialog");
  await setup.getByRole("button", { name: "Next" }).click();
  const codex = setup.getByRole("button", { name: /Codex/ });
  try {
    await codex.click();
    await expect(codex).toContainText("Installing…");
  } finally {
    installResults.get(page)!.resolve();
  }
  await expect(setup.locator(".gs-error")).toContainText("npm ERR! 404 Not Found");
  await expect(codex).toContainText("Not installed yet. Choosing it installs it.");
  await expect(codex).toHaveAttribute("aria-pressed", "false");
  await expect(setup.getByRole("button", { name: /Claude Code/ })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
});

test("the Agent step still works when the ACP registry can't be reached", async ({ page }) => {
  registryOffline = true;
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(error.message));
  const setup = page.locator(".getting-started-dialog");
  await setup.getByRole("button", { name: "Next" }).click();
  await expect(setup.getByRole("heading", { level: 2 })).toHaveText("Pick your agent");
  await expect(setup.getByText(`Couldn't load more agents: Error: ${OFFLINE}`)).toBeVisible();
  const fakeAcp = setup.getByRole("button", { name: /Fake ACP/ });
  await fakeAcp.click();
  await expect(fakeAcp).toHaveAttribute("aria-pressed", "true");
  expect(errors).toEqual([]);
});

test.describe("with suggestions forced on by the environment", () => {
  test.use({ automaticInference: true });

  test("refused switch changes show the host value and say why", async ({ page }) => {
    const setup = page.locator(".getting-started-dialog");
    await setup.getByRole("button", { name: "Next" }).click();
    await setup.getByRole("button", { name: "Next" }).click();
    const inference = setup.getByRole("switch", { name: /Allow suggestions/ });
    await expect(inference).toBeChecked();
    // Two quick changes, both refused: the switch ends on the host's value, not an unconfirmed one.
    await inference.click();
    await inference.click();
    await expect(setup.locator(".gs-error")).toContainText("environment variable");
    await expect(inference).toBeChecked();
    await inference.click();
    await expect(inference).toBeChecked();
  });
});
