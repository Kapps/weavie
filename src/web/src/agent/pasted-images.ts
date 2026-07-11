import { bytesToBase64 } from "../terminal/base64";

const IMAGE_MIME = /^image\/(png|jpeg|gif|webp)$/;
export const MAX_AGENT_IMAGE_BYTES = 5 * 1024 * 1024;

export function takePastedImages(event: ClipboardEvent): Blob[] {
  const items = event.clipboardData?.items;
  if (items === undefined) {
    return [];
  }

  const blobs: Blob[] = [];
  for (let i = 0; i < items.length; i += 1) {
    const item = items[i];
    if (item === undefined || item.kind !== "file" || !IMAGE_MIME.test(item.type)) {
      continue;
    }
    const blob = item.getAsFile();
    if (blob !== null) {
      blobs.push(blob);
    }
  }
  if (blobs.length > 0) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }
  return blobs;
}

export async function encodeAgentImage(blob: Blob): Promise<string> {
  const bytes = new Uint8Array(await blob.arrayBuffer());
  return bytesToBase64(bytes);
}

export function agentImageError(mime: string, dataB64: string): string | null {
  if (!IMAGE_MIME.test(mime)) {
    return `Can't paste that image type (${mime.length === 0 ? "unknown" : mime}) — use PNG, JPEG, GIF, or WebP.`;
  }
  const bytes = Math.floor(dataB64.length / 4) * 3;
  return bytes > MAX_AGENT_IMAGE_BYTES
    ? `That image is ${(bytes / (1024 * 1024)).toFixed(1)} MB — Weavie accepts agent images up to ${MAX_AGENT_IMAGE_BYTES / (1024 * 1024)} MB.`
    : null;
}
