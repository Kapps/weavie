import { base64ToBytes, bytesToBase64 } from "../terminal/base64";

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

export function agentImageBlob(mime: string, dataB64: string): Blob {
  return new Blob([base64ToBytes(dataB64)], { type: mime });
}

export async function encodeAgentImage(blob: Blob): Promise<{ mime: string; dataB64: string }> {
  if (!IMAGE_MIME.test(blob.type)) {
    throw new Error(
      `Can't paste that image type (${blob.type || "unknown"}) — use PNG, JPEG, GIF, or WebP.`,
    );
  }
  if (blob.size === 0) {
    throw new Error("The pasted image was empty.");
  }
  if (!fitsImageLimit(blob)) {
    blob = await resizeImage(blob);
  }
  const bytes = new Uint8Array(await blob.arrayBuffer());
  return { mime: blob.type, dataB64: bytesToBase64(bytes) };
}

function fitsImageLimit(blob: Blob): boolean {
  // The host checks the base64 decoded-byte upper bound, including padding.
  return Math.ceil(blob.size / 3) * 3 < MAX_AGENT_IMAGE_BYTES;
}

async function resizeImage(blob: Blob): Promise<Blob> {
  const url = URL.createObjectURL(blob);
  const image = new Image();
  const canvas = document.createElement("canvas");
  try {
    image.src = url;
    await image.decode();
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    const context = canvas.getContext("2d");
    if (context === null) {
      throw new Error("Couldn't prepare the image for upload.");
    }
    // PNG preserves transparency; oversized animated images become a still frame.
    const mime = blob.type === "image/jpeg" ? "image/jpeg" : "image/png";
    for (;;) {
      context.drawImage(image, 0, 0, canvas.width, canvas.height);
      const encoded = await new Promise<Blob>((resolve, reject) => {
        canvas.toBlob(
          (result) =>
            result === null
              ? reject(new Error("Couldn't encode the image for upload."))
              : resolve(result),
          mime,
          0.9,
        );
      });
      if (fitsImageLimit(encoded)) {
        return encoded;
      }
      if (canvas.width === 1 && canvas.height === 1) {
        throw new Error("Couldn't reduce the image below 5 MB.");
      }
      const scale = Math.min(0.9, Math.sqrt(MAX_AGENT_IMAGE_BYTES / encoded.size) * 0.95);
      canvas.width = Math.max(1, Math.floor(canvas.width * scale));
      canvas.height = Math.max(1, Math.floor(canvas.height * scale));
    }
  } finally {
    URL.revokeObjectURL(url);
    canvas.width = 0;
    canvas.height = 0;
  }
}
