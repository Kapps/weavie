import type { ClientSession } from "../bridge";
import { watchDomSelectionGestures } from "../editor/selection-gestures";
import { createSelectionActions, sendToConversation } from "./selection-actions";

/** Offers explicit actions for a completed selection wholly inside this reply. */
export function installReplySelection(host: HTMLElement, session: ClientSession): () => void {
  const actions = createSelectionActions(host, session, () => null);
  const unwatch = watchDomSelectionGestures(
    host,
    (range, text) => {
      const rect = range.getBoundingClientRect();
      actions.show(
        { label: selectionLabel(text), text },
        { x: rect.left, y: rect.bottom },
        sendToConversation,
      );
    },
    actions.clear,
  );
  return () => {
    unwatch();
    actions.dispose();
  };
}

const LABEL_LENGTH = 60;
/** A one-line chip label for selected text. */
export function selectionLabel(text: string): string {
  const line = text.replace(/\s+/g, " ").trim();
  return line.length <= LABEL_LENGTH ? line : `${line.slice(0, LABEL_LENGTH - 1)}…`;
}
