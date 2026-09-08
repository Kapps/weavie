import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession } from "../harness/acp-session";
import { clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

const selectedEditors = new WeakSet<Page>();
test.use({
  preNavigate: {
    run: async (page) => {
      page.on("websocket", (socket) => {
        socket.on("framesent", ({ payload }) => {
          const frame = payload.toString();
          if (frame.includes("activeChanged") && frame.includes("just plain text")) {
            selectedEditors.add(page);
          }
        });
      });
    },
  },
});

test("reopened ACP transcript hides injected context and preserves user XML", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-transcript-context");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+a");
  await expect.poll(() => selectedEditors.has(page)).toBe(true);

  const prompt = 'Keep my XML: <context ref="user://example">visible text</context>';
  const composer = surface.locator("[data-agent-composer] textarea");
  const userText = surface.locator(".agent-entry-message.agent-tone-user .agent-entry-text");
  await composer.fill(prompt);
  await composer.press("Enter");
  await expect(surface.locator(".agent-tone-assistant")).toContainText("echo: Keep my XML:");
  await expect(userText).toHaveText(prompt);
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  const transcript = await readFile(
    join(weavie.home, ".weavie", "fake-acp-state", "session-transcript-fake-session.log"),
    "utf8",
  );
  expect(transcript).toContain("weavie://instructions");
  expect(transcript).toContain("#selection");
  expect(transcript).toContain("just plain text");

  await runCommand(page, "Unload Session");
  const unloaded = page.locator('.session-chip.unloaded[title^="acp-transcript-context"]');
  await expect(unloaded).toBeVisible();
  await unloaded.click();

  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(surface.locator(".agent-tone-assistant")).toContainText("echo: Keep my XML:");
  await expect(userText).toHaveText(prompt);
  await expect(surface).not.toContainText("weavie://instructions");
  await expect(surface).not.toContainText("#selection");
  await expect(surface).not.toContainText("just plain text");

  await composer.fill("Continue after reopening");
  await composer.press("Enter");
  await expect(surface).toContainText("echo: Continue after reopening");
  await expect(userText).toHaveText([prompt, "Continue after reopening"]);
});
