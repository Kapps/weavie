// Claude terminal image paste. Structured agents use the correlated composer attachment transport instead.

import { agentImageError, encodeAgentImage, takePastedImages } from "../agent/pasted-images";
import { postToBackend } from "../bridge";
import { notify } from "../notify/notify";

export function attachImagePaste(
  container: HTMLElement,
  backendId: () => string,
  slot: string,
): () => void {
  const onPaste = (event: ClipboardEvent): void =>
    void sendPastedImagesFromClipboard(event, backendId(), slot);
  container.addEventListener("paste", onPaste, true);
  return () => container.removeEventListener("paste", onPaste, true);
}

export function sendPastedImagesFromClipboard(
  event: ClipboardEvent,
  backendId: string,
  slot: string,
): boolean {
  if (slot.length === 0) {
    throw new Error("slot is required");
  }
  const blobs = takePastedImages(event);
  for (const blob of blobs) {
    void sendImage(blob, backendId, slot);
  }
  return blobs.length > 0;
}

async function sendImage(blob: Blob, backendId: string, slot: string): Promise<void> {
  sendPastedImage(backendId, slot, blob.type, await encodeAgentImage(blob));
}

export function sendPastedImage(
  backendId: string,
  slot: string,
  mime: string,
  dataB64: string,
): void {
  const error = agentImageError(mime, dataB64);
  if (error !== null) {
    notify("warn", `${error} Resize it and paste again.`);
    return;
  }
  postToBackend(backendId, { type: "term-paste-image", slot, session: "claude", mime, dataB64 });
}
