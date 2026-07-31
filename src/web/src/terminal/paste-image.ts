// Claude terminal image paste. Structured agents use the correlated composer attachment transport instead.

import { agentImageError, encodeAgentImage, takePastedImages } from "../agent/pasted-images";
import type { ClientSession } from "../bridge";
import { notify } from "../notify/notify";

export function attachImagePaste(container: HTMLElement, session: ClientSession): () => void {
  const onPaste = (event: ClipboardEvent): void =>
    void sendPastedImagesFromClipboard(event, session);
  container.addEventListener("paste", onPaste, true);
  return () => container.removeEventListener("paste", onPaste, true);
}

export function sendPastedImagesFromClipboard(
  event: ClipboardEvent,
  session: ClientSession,
): boolean {
  const blobs = takePastedImages(event);
  for (const blob of blobs) {
    void sendImage(blob, session);
  }
  return blobs.length > 0;
}

async function sendImage(blob: Blob, session: ClientSession): Promise<void> {
  sendPastedImage(session, blob.type, await encodeAgentImage(blob));
}

export function sendPastedImage(session: ClientSession, mime: string, dataB64: string): void {
  const error = agentImageError(mime, dataB64);
  if (error !== null) {
    notify("warn", `${error} Resize it and paste again.`);
    return;
  }
  session.feature("terminal.agent").publish("pasteImage", { mime, dataB64 });
}
