import { createMemo, createSignal } from "solid-js";
import { hostConnection, registerHostFeature, selectedSession } from "../bridge";
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

// Cache each host's layout so selecting one of its sessions restores that host's frame.
const [documents, setDocuments] = createSignal<Map<string, LayoutDocument>>(new Map());

function applyDocument(backendId: string, document: LayoutDocument): void {
  setDocuments((current) => new Map(current).set(backendId, document));
}

registerHostFeature((connection) => {
  const offHello = connection.onHello((hello) =>
    applyDocument(connection.id, hello.layout as LayoutDocument),
  );
  const offState = connection.host
    .feature("layout")
    .on<{ document: LayoutDocument }>("state", ({ document }) =>
      applyDocument(connection.id, document),
    );
  return () => {
    offHello();
    offState();
  };
});

/** The active backend's most recent layout document, or null until its first push arrives. */
export const layoutDocument = createMemo<LayoutDocument | null>(
  () => documents().get(selectedSession()?.connection.id ?? "") ?? null,
);

/** Sends an updated layout to the backend that owned the user gesture. */
export function sendLayout(backendId: string, doc: LayoutDocument): void {
  hostConnection(backendId)?.host.feature("layout").publish("changed", { document: doc });
}
