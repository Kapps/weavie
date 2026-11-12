import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// A 2×1 PNG.
const PNG = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAIAAAABCAYAAAD0In+KAAAADklEQVR4nGP4z8AAQv8BD/kD/YURmXYAAAAASUVORK5CYII=",
  "base64",
);

test("an agent reply shows a workspace image in a fixed frame, from a same-origin blob", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "agent-images");
  await writeFile(join(sessionWorktrees(weavie.workspace)[0]!, "shot.png"), PNG);

  await submitAcpDraft(surface, "![the shot](shot.png)");

  const image = surface.locator(".agent-image img");
  await expect(image).toHaveAttribute("alt", "the shot");
  await expect(image).toHaveAttribute("src", /^blob:/);
  await expect
    .poll(() => image.evaluate((element: HTMLImageElement) => element.naturalWidth))
    .toBe(2);
  expect(await surface.locator(".agent-image").evaluate((element) => element.clientHeight)).toBe(
    240,
  );
});

test("a remote image says why it didn't load when remote images are off", async ({ page }) => {
  const surface = await createAcpSession(page, "agent-remote-images");
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.remoteImages","value":false}');
  await expect(surface.locator(".agent-entry-result")).toContainText("setSetting:");

  await submitAcpDraft(surface, "![web](https://example.com/a.png)");

  await expect(surface.locator(".agent-image-status")).toHaveText(
    "Image unavailable: Remote images are off (setting agent.remoteImages).",
  );
  await expect(surface.locator(".agent-image img")).toHaveCount(0);
});
