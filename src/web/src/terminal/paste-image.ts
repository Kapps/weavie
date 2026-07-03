// Claude-pane image paste (the remote path). A browser-served shell can't reach the OS clipboard and the
// remote/headless backend has none, so an image pasted into the terminal is captured here from the DOM paste
// event, shipped to the host as bytes, written to a scratch file on the backend, and its path injected into
// claude (which renders it as [Image #N]). Text paste is untouched — it falls through to xterm. Only fires where
// the DOM paste event reaches the page (a browser-served shell); a native WebView's paste command consumes
// Ctrl+V first. See docs/specs/remote-paste-image.md.

import { postToHost } from "../bridge";
import { notify } from "../notify/notify";
import { bytesToBase64 } from "./base64";

// The image types claude accepts; the host owns the authoritative allowlist + extension mapping (PastedImageMedia).
const IMAGE_MIME = /^image\/(png|jpeg|gif|webp)$/;
// Mirrors PastedImageMedia.MaxBytes (5 MB) — pre-checked here so oversize bytes never ride the bridge.
const MAX_BYTES = 5 * 1024 * 1024;

/**
 * Captures image pastes on the claude terminal container and routes them to the host as a file + injected path;
 * returns a teardown fn. Text pastes pass through to xterm untouched.
 */
export function attachImagePaste(container: HTMLElement, slot: string): () => void {
  const onPaste = (event: ClipboardEvent): void => {
    const items = event.clipboardData?.items;
    if (items === undefined) {
      return;
    }
    // getAsFile() is only valid synchronously during the event, so grab the blobs now.
    const blobs: Blob[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item === undefined || item.kind !== "file" || !IMAGE_MIME.test(item.type)) {
        continue;
      }
      const blob = item.getAsFile();
      if (blob !== null) {
        blobs.push(blob);
      }
    }
    if (blobs.length === 0) {
      return; // text-only paste: let xterm's native handler take it
    }
    // Consume before xterm's textarea sees it, so an image never lands as garbage text.
    event.preventDefault();
    event.stopImmediatePropagation();
    for (const blob of blobs) {
      void sendImage(blob, slot);
    }
  };
  // Capture phase: pre-empt xterm's own textarea paste handler (a descendant of the container).
  container.addEventListener("paste", onPaste, true);
  return () => container.removeEventListener("paste", onPaste, true);
}

async function sendImage(blob: Blob, slot: string): Promise<void> {
  if (blob.size > MAX_BYTES) {
    notify(
      "warn",
      `That image is ${(blob.size / (1024 * 1024)).toFixed(1)} MB — Claude accepts images up to ${MAX_BYTES / (1024 * 1024)} MB. Resize it and paste again.`,
    );
    return;
  }
  const bytes = new Uint8Array(await blob.arrayBuffer());
  postToHost({
    type: "term-paste-image",
    slot,
    session: "claude",
    mime: blob.type,
    dataB64: bytesToBase64(bytes),
  });
}
