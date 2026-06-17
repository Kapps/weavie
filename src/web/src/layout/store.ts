import { createSignal } from "solid-js";
import { onHostMessage, postToHost } from "../bridge";
import type { LayoutDocument } from "./types";

// The latest layout document pushed by the host. The listener is registered at module load — which
// runs before main.tsx sends "ready" — so the host's set-layout reply can never race ahead of it.
const [document, setDocument] = createSignal<LayoutDocument | null>(null);

onHostMessage((message) => {
  if (message.type === "set-layout") {
    setDocument(message.document);
  }
});

/// The most recent layout document from the host, or null until the first push arrives.
export const layoutDocument = document;

/// Sends an updated layout to the host to validate, persist, and broadcast back.
export function sendLayout(doc: LayoutDocument): void {
  postToHost({ type: "layout-changed", document: doc });
}
