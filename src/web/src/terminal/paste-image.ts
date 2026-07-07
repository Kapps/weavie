// Agent image paste (the remote path). A browser-served shell can't reach the OS clipboard and the
// remote/headless backend has none, so an image pasted into an agent input is captured here from the DOM paste
// event, shipped to the host as bytes, written to a scratch file on the backend, and delivered through the
// active provider's native input path. Text paste is untouched.

import { postToHost } from "../bridge";
import { notify } from "../notify/notify";
import { bytesToBase64 } from "./base64";

// The agent image types Weavie accepts; the host owns the authoritative allowlist + extension mapping.
const IMAGE_MIME = /^image\/(png|jpeg|gif|webp)$/;
// Mirrors PastedImageMedia.MaxBytes (5 MB) — pre-checked here so oversize bytes never ride the bridge.
const MAX_BYTES = 5 * 1024 * 1024;

/**
 * Captures image pastes on the Claude terminal container and routes them to the host as an agent image path;
 * returns a teardown fn. Text pastes pass through to xterm untouched.
 */
export function attachImagePaste(container: HTMLElement, slot: string): () => void {
  const onPaste = (event: ClipboardEvent): void => void sendPastedImagesFromClipboard(event, slot);
  // Capture phase: pre-empt xterm's own textarea paste handler (a descendant of the container).
  container.addEventListener("paste", onPaste, true);
  return () => container.removeEventListener("paste", onPaste, true);
}

export function sendPastedImagesFromClipboard(event: ClipboardEvent, slot: string): boolean {
  if (slot.length === 0) {
    throw new Error("slot is required");
  }

  const items = event.clipboardData?.items;
  if (items === undefined) {
    return false;
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
    return false;
  }

  event.preventDefault();
  event.stopImmediatePropagation();
  for (const blob of blobs) {
    void sendImage(blob, slot);
  }
  return true;
}

async function sendImage(blob: Blob, slot: string): Promise<void> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  sendPastedImage(slot, blob.type, bytesToBase64(bytes));
}

/**
 * Ships a pasted image to the backend as `term-paste-image` (the host writes a scratch file on the backend and
 * injects its path into the agent). Oversize is rejected here so it never rides the bridge; the host re-validates as
 * the authoritative gate. Shared by the browser DOM-paste capture and the native-WebView clipboard read.
 */
export function sendPastedImage(slot: string, mime: string, dataB64: string): void {
  const bytes = Math.floor(dataB64.length / 4) * 3;
  if (bytes > MAX_BYTES) {
    notify(
      "warn",
      `That image is ${(bytes / (1024 * 1024)).toFixed(1)} MB — Weavie accepts agent images up to ${MAX_BYTES / (1024 * 1024)} MB. Resize it and paste again.`,
    );
    return;
  }
  postToHost({ type: "term-paste-image", slot, session: "claude", mime, dataB64 });
}
