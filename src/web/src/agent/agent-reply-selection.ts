import type { ClientSession } from "../bridge";
import { createSelectionActions } from "./selection-actions";

/** Offers explicit actions for a completed selection wholly inside this reply. */
export function installReplySelection(host: HTMLElement, session: ClientSession): () => void {
  const actions = createSelectionActions(host, session);
  const show = (): void => {
    const selection = document.getSelection();
    if (
      selection === null ||
      selection.isCollapsed ||
      !host.contains(selection.anchorNode) ||
      !host.contains(selection.focusNode)
    ) {
      actions.clear();
      return;
    }
    const text = selection.toString().trim();
    if (text.length === 0) return;
    const rect = selection.getRangeAt(0).getBoundingClientRect();
    actions.show({ label: selectionLabel(text), text }, { x: rect.left, y: rect.bottom });
  };
  host.addEventListener("pointerup", show);
  host.addEventListener("keyup", show);
  return () => {
    host.removeEventListener("pointerup", show);
    host.removeEventListener("keyup", show);
    actions.dispose();
  };
}

const LABEL_LENGTH = 60;
/** A one-line chip label for selected text. */
export function selectionLabel(text: string): string {
  const line = text.replace(/\s+/g, " ").trim();
  return line.length <= LABEL_LENGTH ? line : `${line.slice(0, LABEL_LENGTH - 1)}…`;
}
