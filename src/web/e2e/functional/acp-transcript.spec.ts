import { readdir, readFile, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession } from "../harness/acp-session";
import { clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { ZOOM_IMAGE_SRC } from "../harness/git-workspace";
import { pastePng } from "../harness/pasted-image";

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

test("reopened ACP transcript preserves images and clean history and resumes its existing BTW fork", async ({
  page,
  weavie,
}) => {
  const registryPath = join(weavie.home, ".weavie", "acp", "custom.json");
  const registry = JSON.parse(await readFile(registryPath, "utf8"));
  registry.agents[0].args.push("--flatten-replay");
  await writeFile(registryPath, JSON.stringify(registry));
  await runCommand(page, "Manage ACP Agents");
  const registryDialog = page.locator(".acp-registry-dialog");
  await registryDialog.getByRole("button", { name: "Reload", exact: true }).click();
  await expect(
    page.locator(".toast", { hasText: "ACP agent definitions were reloaded." }),
  ).toBeVisible();
  await registryDialog.getByRole("button", { name: "Close", exact: true }).click();

  const surface = await createAcpSession(page, "acp-transcript-context");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+a");
  await expect.poll(() => selectedEditors.has(page)).toBe(true);

  const prompt = [
    "Keep my XML:",
    "weavie://instructions",
    '<context ref="weavie://instructions">',
    "visible user text",
    "</context>",
  ].join("\n");
  const composer = surface.locator("[data-agent-composer] textarea");
  const userText = surface
    .locator(".agent-entry-message.agent-tone-user .agent-entry-text")
    .filter({ hasText: "Keep my XML:" });
  const encodedImage = ZOOM_IMAGE_SRC.split(",")[1]!;
  await pastePng(composer, encodedImage);
  await expect(surface.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  await composer.fill(prompt);
  await composer.press("Enter");
  await expect(surface.locator(".agent-tone-assistant")).toContainText("echo: Keep my XML:");
  await expect(userText).toHaveText(prompt);
  const image = surface.locator(".agent-entry-media");
  await expect(image).toHaveAttribute("src", ZOOM_IMAGE_SRC);
  await expect(image).toHaveJSProperty("naturalWidth", 200);
  await expect(image).toHaveJSProperty("naturalHeight", 80);
  await expect(surface).not.toContainText("data:image/");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  const workspacesRoot = join(weavie.home, ".weavie", "workspaces");
  const stagedImages = (await readdir(workspacesRoot, { recursive: true })).filter(
    (path) => path.split(/[/\\]/).includes("pasted-images") && path.endsWith(".png"),
  );
  expect(stagedImages).toHaveLength(1);
  const stagedImage = join(workspacesRoot, stagedImages[0]!);
  expect(await readFile(stagedImage)).toEqual(Buffer.from(encodedImage, "base64"));
  const transcript = await readFile(
    join(weavie.home, ".weavie", "fake-acp-state", "session-transcript-fake-session.log"),
    "utf8",
  );
  const blocks: {
    type: string;
    data?: string;
    mimeType?: string;
    annotations?: { audience: string[] };
    resource?: { uri: string; text: string };
  }[] = JSON.parse(transcript);
  expect(blocks.find((block) => block.type === "image")).toMatchObject({
    mimeType: "image/png",
    data: encodedImage,
  });
  const context = blocks.filter((block) => block.resource !== undefined);
  expect(context).toHaveLength(2);
  expect(context.map((block) => block.annotations?.audience)).toEqual([
    ["assistant"],
    ["assistant"],
  ]);
  expect(context[0]?.resource?.uri).toBe("weavie://instructions");
  expect(context[1]?.resource?.uri).toContain("#selection");
  expect(context[1]?.resource?.text).toContain("just plain text");

  await composer.fill("/btw side question before reopening");
  await composer.press("Enter");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("echo: side question before reopening");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await reply.fill("identify-session");
  await reply.press("Enter");
  const statePath = join(weavie.home, ".weavie", "fake-acp-state");
  const forks = await readFile(join(statePath, "forks.log"), "utf8");
  const fork = forks.trim().split("->");
  expect(fork).toHaveLength(2);
  const childId = fork[1]!;
  const identity = aside.locator(".agent-tone-assistant", { hasText: `session: ${childId}` });
  await expect(identity).toHaveCount(1);
  await expect(aside.getByRole("button", { name: "Reply", exact: true })).toBeEnabled();
  const conversationId = await aside.getAttribute("data-agent-aside");
  const messages = surface.locator(".agent-entry-message .agent-entry-main");
  const displayed = await messages.allTextContents();

  await runCommand(page, "Unload Session");
  const unloaded = page.locator('.session-chip.unloaded[title^="acp-transcript-context"]');
  await expect(unloaded).toBeVisible();
  await unlink(stagedImage);
  await unloaded.click();

  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(aside).toHaveAttribute("data-agent-aside", conversationId!);
  await expect.poll(() => messages.allTextContents()).toEqual(displayed);
  await expect(userText).toHaveText(prompt);
  await expect(image).toHaveAttribute("src", ZOOM_IMAGE_SRC);
  await expect(image).toHaveJSProperty("naturalWidth", 200);
  await expect(image).toHaveJSProperty("naturalHeight", 80);
  await expect(surface).not.toContainText("data:image/");
  await expect(surface).not.toContainText("You are running embedded in Weavie");
  await expect(surface).not.toContainText("#selection");
  await expect(surface).not.toContainText("just plain text");

  await composer.fill("hold");
  await composer.press("Enter");
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await reply.fill("identify-session");
  await reply.press("Enter");
  await expect(identity).toHaveCount(2);
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");

  await composer.fill("main advances independently");
  await composer.press("Enter");
  await expect(surface).toContainText("steered: main advances independently");
  await expect(aside).not.toContainText("main advances independently");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await reply.fill("side continues independently");
  await reply.press("Enter");
  await expect(aside).toContainText("echo: side continues independently");
  const main = surface.locator(".agent-transcript > .agent-virtual-row > .agent-entry-message");
  await expect(main.filter({ hasText: "side continues independently" })).toHaveCount(0);
  await expect(surface.locator(".agent-tone-error")).toHaveCount(0);
  expect(await readFile(join(statePath, "forks.log"), "utf8")).toBe(forks);
  const loads = (await readFile(join(statePath, "loads.log"), "utf8")).trim().split(/\r?\n/);
  expect(loads.filter((id) => id === fork[0])).toHaveLength(1);
  expect(loads.filter((id) => id === childId)).toHaveLength(2);
});
