import { mkdirSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import WebSocket from "ws";
import {
  activeSessionSlot,
  createSession,
  openFile,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";

interface SessionAddress {
  slot: string;
  incarnation: string;
}

async function selectedSessionAddress(
  page: import("@playwright/test").Page,
  hostUrl: string,
  token: string,
): Promise<SessionAddress> {
  const slot = await page.locator(".session-chip.active").getAttribute("data-session-slot");
  if (slot === null) {
    throw new Error("the selected session chip has no slot");
  }

  const endpoint = new URL(hostUrl);
  endpoint.protocol = endpoint.protocol === "https:" ? "wss:" : "ws:";
  endpoint.pathname = "/weavie-bridge";
  endpoint.search = new URLSearchParams({ token }).toString();
  const socket = new WebSocket(endpoint);
  return new Promise<SessionAddress>((resolve, reject) => {
    socket.on("error", reject);
    socket.on("open", () => {
      socket.send(
        JSON.stringify({
          scope: "host",
          session: null,
          kind: "request",
          requestId: "media-test-hello",
          feature: "connection",
          name: "hello",
          payload: {},
          error: null,
        }),
      );
    });
    socket.on("message", (data) => {
      const envelope = JSON.parse(data.toString()) as {
        kind?: string;
        requestId?: string;
        payload?: { sessions?: { id: string; address: SessionAddress | null }[] };
      };
      if (envelope.kind !== "response" || envelope.requestId !== "media-test-hello") {
        return;
      }
      const address = envelope.payload?.sessions?.find((session) => session.id === slot)?.address;
      socket.close();
      if (address === null || address === undefined) {
        reject(new Error(`the selected session '${slot}' is not live`));
      } else {
        resolve(address);
      }
    });
  });
}

// Every workspace's persisted session overlay concatenated ("" until the host has written one) — polled to
// know the debounced editor-session change landed on disk before a reload, instead of sleeping past it.
function persistedSessions(home: string): string {
  const root = join(home, ".weavie", "workspaces");
  try {
    return readdirSync(root)
      .map((id) => {
        try {
          return readFileSync(join(root, id, "sessions.json"), "utf8");
        } catch {
          return "";
        }
      })
      .join("\n");
  } catch {
    return "";
  }
}

// Open the seeded PNG → the media pane streams it from the authenticated workspace endpoint; naturalWidth
// proves the bytes decoded. Reload restores the persisted media tab into the pane, never a Monaco working copy.
test("image renders in the media pane and survives reload", async ({ page, weavie }) => {
  await openFile(page, "pixel.png");

  const img = page.locator(".editor-media img");
  await expect(img).toBeVisible();
  await expect(img).toHaveJSProperty("naturalWidth", 8);
  expect(new URL((await img.getAttribute("src")) as string).pathname).toBe(
    "/weavie-media/pixel.png",
  );

  // Reload: the media tab restores from the persisted editor session, back into the pane (the restore path's
  // media guard — without it Monaco would read the binary as UTF-8 text). Wait for the debounced session
  // persist to land host-side first, or the reload races it and restores an empty tab set.
  await expect.poll(() => persistedSessions(weavie.home)).toContain("pixel.png");
  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await page.locator(".session-inbox-row").click();
  await expect(page.locator(".editor-media img")).toBeVisible();
  await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
});

// Video uses the same range-capable endpoint. Metadata readiness proves a paused, metadata-only load settles
// without waiting for the whole video, while the explicit Range request pins seeking support.
test("video file opens as an autoplaying <video controls> in the media pane @cross", async ({
  page,
}) => {
  await openFile(page, "clip.webm");

  const video = page.locator(".editor-media video");
  await expect(video).toBeAttached();
  await expect(video).toHaveAttribute("controls", "");
  await expect(video).toHaveAttribute("preload", "metadata");
  await expect(video).toHaveAttribute("autoplay", "");
  await expect(page.locator(".editor-media-notice")).toHaveCount(0);
  await expect(page.locator('.editor-surface[data-kind="editor"]')).toHaveClass(/\bactive\b/);
  const src = (await video.getAttribute("src")) as string;
  expect(new URL(src).pathname).toBe("/weavie-media/clip.webm");
  const range = await page.request.get(src, { headers: { Range: "bytes=2-5" } });
  expect(range.status()).toBe(206);
  expect(await range.body()).toEqual(Buffer.from([0xdf, 0xa3, 0x9f, 0x42]));
});

// Mounting a restored media overlay must preserve the pane that owns keyboard focus. Otherwise autoplaying
// video moves focus into the editor, changing Ctrl+Tab from session navigation to editor-tab navigation.
test("autoplaying video preserves terminal focus across keyboard session switches", async ({
  page,
}) => {
  await openFile(page, "clip.webm");
  const videoSession = await activeSessionSlot(page);

  await createSession(page, { branch: "e2e/video-focus", provider: "claude" });
  await expect(page.locator(".session-chip")).toHaveCount(2);
  const terminalSession = await waitForSessionSwitch(page, videoSession);
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  await shell.locator(".pane-head").click();
  await expect(shell).toHaveClass(/\bactive\b/);

  await page.keyboard.press("Control+Shift+Tab");
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    videoSession,
  );
  await expect(page.locator(".editor-media video")).toHaveAttribute("autoplay", "");
  await expect(shell).toHaveClass(/\bactive\b/);

  await page.keyboard.press("Control+Tab");
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    terminalSession,
  );
  await expect(shell).toHaveClass(/\bactive\b/);
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
    await expect(page.locator(".editor-media-notice")).toHaveCount(0);
  });
});

// A text file and a media file coexist: switching between them swaps the overlay in and out without
// disturbing the Monaco working copy underneath.
test("switching between a text tab and a media tab keeps both healthy", async ({ page }) => {
  await openFile(page, "hello.ts");
  await openFile(page, "pixel.png");
  const image = page.locator(".editor-media img");
  await expect(image).toBeVisible();
  const firstSource = await image.getAttribute("src");

  await page.locator(".editor-tab", { hasText: "hello.ts" }).click();
  await expect(page.locator(".editor-media")).toHaveCount(0);
  await expect(page.locator(".monaco-editor .view-lines").first()).toContainText("greet");

  await page.locator(".editor-tab", { hasText: "pixel.png" }).click();
  await expect(page.locator(".editor-media img")).toBeVisible();
  expect(await page.locator(".editor-media img").getAttribute("src")).toBe(firstSource);
});

// Scratch is session-owned just like workspace files. Same-named media in two scratch stores must render from
// the exact owner, then switch back to the first owner's persisted tab without crossing authorization routes.
test("same-named scratch media switches to the incoming session route", async ({
  page,
  weavie,
}) => {
  const createScratchMedia = async (): Promise<string> => {
    await page.locator(".editor-empty-action", { hasText: "New File" }).click();
    const untitled = await page.locator(".editor-tab.active").getAttribute("title");
    if (untitled === null) {
      throw new Error("the scratch tab has no path");
    }
    const path = join(dirname(untitled), "shared.png");
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, readFileSync(join(weavie.workspace, "pixel.png")));
    return path;
  };

  const openScratch = async (path: string): Promise<void> => {
    const address = await selectedSessionAddress(page, weavie.url, weavie.token);
    await page.evaluate(
      ({ path, session }) => {
        window.__weavieReceive?.(
          JSON.stringify({
            scope: "session",
            session,
            kind: "event",
            requestId: null,
            feature: "editor",
            name: "openFile",
            payload: { path, line: 1, scratch: true },
            error: null,
          }),
        );
      },
      { path, session: address },
    );
    await expect(page.locator(".editor-media img")).toHaveJSProperty("naturalWidth", 8);
  };

  const firstPath = await createScratchMedia();
  await openScratch(firstPath);
  const first = new URL((await page.locator(".editor-media img").getAttribute("src")) as string);
  await expect.poll(() => persistedSessions(weavie.home)).toContain("shared.png");

  const firstSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/scratch-media", provider: "claude" });
  await expect(page.locator(".session-chip")).toHaveCount(2);
  await waitForSessionSwitch(page, firstSlot);
  const secondPath = await createScratchMedia();
  await openScratch(secondPath);
  const second = new URL((await page.locator(".editor-media img").getAttribute("src")) as string);
  expect(second.searchParams.get("session")).not.toBe(first.searchParams.get("session"));
  expect(second.searchParams.get("path")).not.toBe(first.searchParams.get("path"));

  const incoming = page.locator(".session-chip:not(.active)");
  const incomingTitle = await incoming.getAttribute("title");
  await incoming.click();
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "title",
    incomingTitle as string,
  );
  await expect
    .poll(async () => {
      const src = await page.locator(".editor-media img").getAttribute("src");
      return new URL(src as string).searchParams.get("session");
    })
    .toBe(first.searchParams.get("session"));
});

// A worktree switch changes both authorization session and absolute path. They must publish atomically: mixing
// the incoming session with the outgoing path creates a confined-route 404 whose late error can mask success.
test("worktree media switches owner and path without a transient 404", async ({ page, weavie }) => {
  await openFile(page, "pixel.png");
  const image = page.locator(".editor-media img");
  await expect(image).toHaveJSProperty("naturalWidth", 8);
  const first = new URL((await image.getAttribute("src")) as string);
  await expect.poll(() => persistedSessions(weavie.home)).toContain("pixel.png");

  const firstSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/worktree-media", provider: "claude" });
  await expect(page.locator(".session-chip")).toHaveCount(2);
  await waitForSessionSwitch(page, firstSlot);
  await openFile(page, "pixel.png");
  await expect
    .poll(async () => {
      const src = await image.getAttribute("src");
      return src === null ? null : new URL(src).searchParams.get("session");
    })
    .not.toBe(first.searchParams.get("session"));
  await expect(image).toHaveJSProperty("naturalWidth", 8);
  const second = new URL((await image.getAttribute("src")) as string);
  expect(second.searchParams.get("session")).not.toBe(first.searchParams.get("session"));
  expect(second.searchParams.get("path")).not.toBe(first.searchParams.get("path"));

  const failedRequests: string[] = [];
  page.on("response", (response) => {
    if (new URL(response.url()).pathname.startsWith("/weavie-media/") && response.status() >= 400) {
      failedRequests.push(`${response.status()} ${response.url()}`);
    }
  });

  for (const expected of [first, second]) {
    await page.locator(".session-chip:not(.active)").click();
    await expect
      .poll(async () => {
        const src = await image.getAttribute("src");
        return src === null ? null : new URL(src).searchParams.get("session");
      })
      .toBe(expected.searchParams.get("session"));
    await expect(image).toHaveJSProperty("naturalWidth", 8);
    await expect(page.locator(".editor-media-notice")).toHaveCount(0);
  }

  expect(failedRequests).toEqual([]);
});
