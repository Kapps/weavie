import { createSignal } from "solid-js";

/** What a gesture on a block selected, as the composer attachment it becomes. */
export interface BlockSelection {
  label: string;
  text: string;
}

// The block row a gesture is in progress on stays mounted until the gesture ends.
const [selecting, setSelecting] = createSignal<string | null>(null);
export const selectingBlockEntry = selecting;

export function noteBlockSelecting(entryId: string, active: boolean): void {
  setSelecting((current) => (active ? entryId : current === entryId ? null : current));
}
