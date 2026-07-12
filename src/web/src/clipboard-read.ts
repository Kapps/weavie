import { onHostMessage, postToLocalHost } from "./bridge";

export interface ClipboardImage {
  mime: string;
  dataB64: string;
}

let sequence = 0;
const pendingText = new Map<string, (text: string) => void>();
const pendingImages = new Map<string, (image: ClipboardImage) => void>();
const READ_TIMEOUT_MS = 3000;

onHostMessage((message) => {
  if (message.type === "clipboard-content") {
    pendingText.get(message.id)?.(message.text);
  } else if (message.type === "clipboard-image-content") {
    pendingImages.get(message.id)?.({ mime: message.mime, dataB64: message.dataB64 });
  }
});

export const readClipboardText = (): Promise<string> =>
  requestFromLocalHost((id) => postToLocalHost({ type: "clipboard-read", id }), pendingText);

export const readClipboardImage = (): Promise<ClipboardImage> =>
  requestFromLocalHost(
    (id) => postToLocalHost({ type: "clipboard-read-image", id }),
    pendingImages,
  );

function requestFromLocalHost<T>(
  send: (id: string) => void,
  pending: Map<string, (value: T) => void>,
): Promise<T> {
  const id = `clip${++sequence}`;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error("the host didn't respond"));
    }, READ_TIMEOUT_MS);
    pending.set(id, (value) => {
      clearTimeout(timer);
      pending.delete(id);
      resolve(value);
    });
    send(id);
  });
}
