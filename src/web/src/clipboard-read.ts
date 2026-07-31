import { hostConnection, LOCAL_BACKEND_ID } from "./bridge";

export interface ClipboardImage {
  mime: string;
  dataB64: string;
}

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
