// Shell-style Up/Down recall of a session's own submitted prompts. The prompts already live in the pane
// transcript (user-message / user-steer updates), so history is derived from there — genuinely per-session
// and durable across reloads, with no separate store. `cursor` walks the list from newest; `null` means the
// caller is editing a fresh draft, `stash` holds that draft while the user browses older prompts.

import type { AgentPaneUpdate } from "../bridge";

export interface HistoryCursor {
  cursor: number | null;
  stash: string;
}

export interface HistoryRecall {
  text: string;
  next: HistoryCursor;
}

export const IDLE_CURSOR: HistoryCursor = { cursor: null, stash: "" };

/** The session's submitted prompts oldest-first, dropping blanks and adjacent duplicates. */
export function submittedPrompts(messages: readonly AgentPaneUpdate[]): string[] {
  const prompts: string[] = [];
  for (const message of messages) {
    if (message.type !== "user-message" && message.type !== "user-steer") {
      continue;
    }
    const text = message.text?.trim();
    if (text === undefined || text.length === 0 || text === prompts[prompts.length - 1]) {
      continue;
    }
    prompts.push(text);
  }
  return prompts;
}

/** Recall the previous (older) prompt, stashing the live draft on the first step. Null when none applies. */
export function recallPrevious(
  history: readonly string[],
  state: HistoryCursor,
  draft: string,
): HistoryRecall | null {
  if (history.length === 0) {
    return null;
  }
  if (state.cursor === null) {
    const cursor = history.length - 1;
    return { text: history[cursor]!, next: { cursor, stash: draft } };
  }
  const cursor = Math.max(0, state.cursor - 1);
  return { text: history[cursor]!, next: { cursor, stash: state.stash } };
}

/** Recall the next (newer) prompt, restoring the stashed draft past the newest. Null when already idle. */
export function recallNext(history: readonly string[], state: HistoryCursor): HistoryRecall | null {
  if (state.cursor === null) {
    return null;
  }
  if (state.cursor >= history.length - 1) {
    return { text: state.stash, next: IDLE_CURSOR };
  }
  const cursor = state.cursor + 1;
  return { text: history[cursor]!, next: { cursor, stash: state.stash } };
}

/** Whether the caret sits on the textarea's first line (so Up should recall rather than move the caret). */
export function caretOnFirstLine(value: string, selectionStart: number): boolean {
  return !value.slice(0, selectionStart).includes("\n");
}

/** Whether the caret sits on the textarea's last line (so Down should recall rather than move the caret). */
export function caretOnLastLine(value: string, selectionEnd: number): boolean {
  return !value.slice(selectionEnd).includes("\n");
}
