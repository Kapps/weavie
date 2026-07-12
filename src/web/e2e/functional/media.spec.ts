import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Every workspace's persisted editor session concatenated ("" until the host has written one) — polled to
// know the debounced editor-session-changed landed on disk before a reload, instead of sleeping past it.
function persistedSessions(home: string): string {
  const root = join(home, ".weavie", "workspaces");
  try {
    return readdirSync(root)
      .map((id) => {
        try {
          return readFileSync(join(root, id, "editor-session.json"), "utf8");
        } catch {
          return "";
        }
      })
      .join("\n");
  } catch {
    return "";
  }
}

// Open the seeded PNG → the media pane renders it from real host bytes (fs-stat → fs-read-bytes → Blob;
// naturalWidth proves the bytes decoded); reload → the persisted media tab restores into the pane, never a
// Monaco working copy. (The fs-change re-fetch is unit-tested in media-store.test.ts; the on-disk watcher
// only covers LSP extensions, so an external image edit doesn't push one — Claude's edits do, for any path.)
test("image renders in the media pane and survives reload", async ({ page, weavie }) => {
  await openFile(page, "pixel.png");

  const img = page.locator(".editor-media img");
  await expect(img).toBeVisible();
  await expect(img).toHaveJSProperty("naturalWidth", 8);
  expect(await img.getAttribute("src")).toMatch(/^blob:/);

  // Reload: the media tab restores from the persisted editor session, back into the pane (the restore path's
  // media guard — without it Monaco would read the binary as UTF-8 text). Wait for the debounced session
  // persist to land host-side first, or the reload races it and restores an empty tab set.
  await expect.poll(() => persistedSessions(weavie.home)).toContain("pixel.png");
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(page.locator(".editor-media img")).toBeVisible();
  await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
});

// The video path shares the image byte pipeline; assert the pane mounts a <video controls> with a Blob URL
// (the seed isn't a decodable video — decode is the browser's job, not the pipeline under test). Autoplay
// defaults on (editor.videoAutoplay), asserted via the attribute for the same reason.
test("video file opens as an autoplaying <video controls> in the media pane", async ({ page }) => {
  await openFile(page, "clip.webm");

  const video = page.locator(".editor-media video");
  await expect(video).toBeAttached();
  await expect(video).toHaveAttribute("controls", "");
  await expect(video).toHaveAttribute("autoplay", "");
  expect(await video.getAttribute("src")).toMatch(/^blob:/);
});

// Turning editor.videoAutoplay off (here via the settings MCP round-trip) drops the autoplay attribute from
// the mounted element — live, so the assertion converges whether the setting lands before or after the mount.
test.describe(() => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "editor.videoAutoplay", value: false } },
      ],
    },
  });

  test("disabling editor.videoAutoplay opens videos paused", async ({ page }) => {
    await openFile(page, "clip.webm");

    const video = page.locator(".editor-media video");
    await expect(video).toBeAttached();
    await expect(video).not.toHaveAttribute("autoplay", "");
    await expect(video).toHaveAttribute("controls", "");
  });
});

// A text file and a media file coexist: switching between them swaps the overlay in and out without
// disturbing the Monaco working copy underneath.
test("switching between a text tab and a media tab keeps both healthy", async ({ page }) => {
  await openFile(page, "hello.ts");
  await openFile(page, "pixel.png");
  await expect(page.locator(".editor-media img")).toBeVisible();

  await page.locator(".editor-tab", { hasText: "hello.ts" }).click();
  await expect(page.locator(".editor-media")).toHaveCount(0);
  await expect(page.locator(".monaco-editor .view-lines").first()).toContainText("greet");

  await page.locator(".editor-tab", { hasText: "pixel.png" }).click();
  await expect(page.locator(".editor-media img")).toBeVisible();
});
