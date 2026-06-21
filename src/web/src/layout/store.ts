import { createSignal } from "solid-js";
import { onHostMessage, postToHost } from "../bridge";
import type { LayoutDocument, LayoutNode } from "./types";

// The default layout (mirrors Weavie.Core.Layout's seeded default): a left column stacking the Claude and
// shell terminals beside the editor, 40/60. Shown until the host pushes the persisted layout.
export const DEFAULT_LAYOUT_ROOT: LayoutNode = {
  type: "split",
  dir: "row",
  weights: [0.4, 0.6],
  children: [
    {
      type: "split",
      dir: "column",
      weights: [0.5, 0.5],
      children: [
        { type: "pane", id: "p_claude", kind: "terminal:claude" },
        { type: "pane", id: "p_shell", kind: "terminal:shell" },
      ],
    },
    { type: "pane", id: "p_editor", kind: "editor" },
  ],
};

// The latest layout document pushed by the host. The listener is registered at module load (before
// main.tsx sends "ready") so the host's set-layout reply can never race ahead of it.
const [document, setDocument] = createSignal<LayoutDocument | null>(null);

onHostMessage((message) => {
  if (message.type === "set-layout") {
    setDocument(message.document);
  }
});

/** The most recent layout document from the host, or null until the first push arrives. */
export const layoutDocument = document;

/** Sends an updated layout to the host to validate, persist, and broadcast back. */
export function sendLayout(doc: LayoutDocument): void {
  postToHost({ type: "layout-changed", document: doc });
}
