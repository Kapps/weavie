import { createMemo, createSignal } from "solid-js";
import { activeBackendId, onSessionMessage, postToBackend } from "../bridge";
import type { LayoutDocument, LayoutNode } from "./types";

// The default layout (mirrors Weavie.Core.Layout's seeded default): a left column stacking the agent and
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

// A backend's ready replay can arrive while another backend drives the page. Cache every workspace layout so
// activating that backend restores its frame instead of retaining whichever layout happened to paint first.
const [documents, setDocuments] = createSignal<Map<string, LayoutDocument>>(new Map());

onSessionMessage((message, backendId) => {
  if (message.type === "set-layout") {
    setDocuments((current) => new Map(current).set(backendId, message.document));
  }
});

/** The active backend's most recent layout document, or null until its first push arrives. */
export const layoutDocument = createMemo<LayoutDocument | null>(
  () => documents().get(activeBackendId()) ?? null,
);

/** Sends an updated layout to the backend that owned the user gesture. */
export function sendLayout(backendId: string, doc: LayoutDocument): void {
  postToBackend(backendId, { type: "layout-changed", document: doc });
}
