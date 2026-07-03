import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";

// Remote image paste into the claude pane. The deterministic C# tests inject the `term-paste-image` message
// straight into HostCore, so they never exercise the ONE link that lives only in the browser: a real DOM
// `paste` event carrying an image File, captured on the claude terminal container, must post
// `term-paste-image` — and the host must then write the bytes to a per-session scratch file. This spec pins
// that browser-capture → bridge → host-write chain (paste-image.ts + its TerminalView wiring), which no other
// test covers. The PTY path-injection line is downstream of the write with no branch between and is asserted
// by HostCorePasteImageTests at the NoopTerminal seam; the real [Image #N] chip needs the real claude.
//
// A 150-byte valid 64x64 PNG — the pasted image's exact bytes.
const PNG_B64 =
  "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAXUlEQVR42u3PMQ0AIAwAMJTsnhxkI2I3NxLQsGNfkxroqohRcWYtAQEBAQEBAQEBAQEBAQEBAQEBAQEBAYF2IPcd9SpHCQgICAgICAgICAgICAgICAgICAgICAi0fZNauTzyRETRAAAAAElFTkSuQmCC";

// Recursively collect every file path under `dir` (the host's isolated HOME is tiny, so a full walk is cheap).
function walk(dir: string): string[] {
  const out: string[] = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      out.push(...walk(full));
    } else {
      out.push(full);
    }
  }
  return out;
}

// The pasted image lands at ~/.weavie/workspaces/<id>/pasted-images/<worktree-digest>/paste-N.<ext>; the id
// and digest are only known at runtime, so glob for any paste-*.png under the host's isolated HOME.
function pastedPngs(home: string): string[] {
  const root = join(home, ".weavie", "workspaces");
  try {
    return walk(root).filter((p) => /[/\\]pasted-images[/\\].*paste-\d+\.png$/.test(p));
  } catch {
    return []; // the workspaces dir may not exist until the first paste creates it
  }
}

// Dispatch a synthetic `paste` on the claude pane's hidden helper textarea (where a real paste gesture lands)
// with a DataTransfer carrying the given item. An image item is a File (kind "file"); a text item is a string.
// Returns nothing — effects are observed via the WebSocket spy and the backend filesystem.
async function pasteInto(
  page: import("@playwright/test").Page,
  item: { kind: "image"; b64: string; mime: string } | { kind: "text"; text: string },
): Promise<void> {
  await page.evaluate((arg) => {
    const target =
      document.querySelector<HTMLElement>(
        '.terminal-surface[data-kind="terminal:claude"] .xterm-helper-textarea',
      ) ??
      document.querySelector<HTMLElement>('.terminal-surface[data-kind="terminal:claude"] .term');
    if (target === null) {
      throw new Error("claude terminal container not found");
    }
    const dt = new DataTransfer();
    if (arg.kind === "image") {
      const bin = atob(arg.b64);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) {
        bytes[i] = bin.charCodeAt(i);
      }
      dt.items.add(new File([bytes], "pasted.png", { type: arg.mime }));
    } else {
      dt.setData("text/plain", arg.text);
    }
    const event = new ClipboardEvent("paste", {
      clipboardData: dt,
      bubbles: true,
      cancelable: true,
    });
    target.focus();
    target.dispatchEvent(event);
  }, item);
}

test("a real image-paste DOM event on the claude pane writes the bytes to a backend scratch file", async ({
  page,
  weavie,
}) => {
  // The claude pane must be mounted (its capture-phase paste listener is attached on mount).
  await expect(page.locator('.terminal-surface[data-kind="terminal:claude"] .term')).toBeVisible();

  // Spy on the outbound bridge socket so we can see exactly which host-bound messages the paste produces —
  // isolating the browser capture from its downstream host effect. `send` is on the prototype, so patching it
  // after the socket opened still intercepts every send.
  await page.evaluate(() => {
    (window as unknown as { __PASTE_MSGS__: unknown[] }).__PASTE_MSGS__ = [];
    const original = WebSocket.prototype.send;
    WebSocket.prototype.send = function (
      data: string | ArrayBufferLike | Blob | ArrayBufferView,
    ): void {
      if (typeof data === "string" && data.includes('"term-paste-image"')) {
        (window as unknown as { __PASTE_MSGS__: unknown[] }).__PASTE_MSGS__.push(JSON.parse(data));
      }
      original.call(this, data as string);
    };
  });

  await pasteInto(page, { kind: "image", b64: PNG_B64, mime: "image/png" });

  // The browser capture fired: exactly one term-paste-image, targeting the claude session, carrying the mime.
  const msgs = await page.evaluate(
    () => (window as unknown as { __PASTE_MSGS__: Array<Record<string, string>> }).__PASTE_MSGS__,
  );
  expect(msgs).toHaveLength(1);
  expect(msgs[0]).toMatchObject({ type: "term-paste-image", session: "claude", mime: "image/png" });

  // The host wrote the bytes to a per-session scratch file, byte-for-byte, with the .png extension.
  const expected = Buffer.from(PNG_B64, "base64");
  await expect.poll(() => pastedPngs(weavie.home).length, { timeout: 15_000 }).toBe(1);
  const written = pastedPngs(weavie.home)[0];
  expect(written).toMatch(/[/\\]pasted-images[/\\].*[/\\]paste-1\.png$/);
  expect(readFileSync(written).equals(expected)).toBe(true);
});

test("a text-only paste on the claude pane never posts term-paste-image (falls through to xterm)", async ({
  page,
  weavie,
}) => {
  await expect(page.locator('.terminal-surface[data-kind="terminal:claude"] .term')).toBeVisible();

  await page.evaluate(() => {
    (window as unknown as { __PASTE_MSGS__: unknown[] }).__PASTE_MSGS__ = [];
    const original = WebSocket.prototype.send;
    WebSocket.prototype.send = function (
      data: string | ArrayBufferLike | Blob | ArrayBufferView,
    ): void {
      if (typeof data === "string" && data.includes('"term-paste-image"')) {
        (window as unknown as { __PASTE_MSGS__: unknown[] }).__PASTE_MSGS__.push(JSON.parse(data));
      }
      original.call(this, data as string);
    };
  });

  await pasteInto(page, { kind: "text", text: "just some pasted text" });

  // The image predicate rejected the text item: no host-bound image message, and nothing written on disk.
  await page.waitForTimeout(1000);
  const msgs = await page.evaluate(
    () => (window as unknown as { __PASTE_MSGS__: unknown[] }).__PASTE_MSGS__,
  );
  expect(msgs).toHaveLength(0);
  expect(pastedPngs(weavie.home)).toHaveLength(0);
});
