import { hostConnection, LOCAL_BACKEND_ID } from "./bridge";

export interface ClipboardImage {
  mime: string;
  dataB64: string;
}

export type ClipboardContent =
  | { kind: "image"; mime: string; dataB64: string }
  | { kind: "text"; text: string }
  | { kind: "empty" };

function localClipboard() {
  const connection = hostConnection(LOCAL_BACKEND_ID);
  if (connection === undefined) {
    throw new Error("The local host is unavailable.");
  }
  return connection.host.feature("clipboard");
}

export const readClipboardText = async (): Promise<string> => {
  const result = await localClipboard().request<{ text: string }>("read", {});
  return result.text;
};

export const readClipboardImage = (): Promise<ClipboardImage> =>
  localClipboard().request("readImage", {});

/** Reads image-capable input clipboards in the same image-first order on every agent surface. */
export const readClipboardContent = async (): Promise<ClipboardContent> => {
  const image = await readClipboardImage();
  if (image.mime.length > 0) {
    return { kind: "image", mime: image.mime, dataB64: image.dataB64 };
  }
  const text = await readClipboardText();
  return text.length > 0 ? { kind: "text", text } : { kind: "empty" };
};
