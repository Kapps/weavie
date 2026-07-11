// The slash-menu trigger: the composer draft is a slash command while it starts with "/" and holds no
// whitespace yet (still typing the command name); once a space begins the prompt, the menu closes. Kept
// caret-free and pure so it's trivially testable and provider-agnostic — it filters whatever entries the
// capability interface supplied.

import type { AgentSlashEntry } from "../bridge";

/** The query after the leading slash, or null when the draft isn't a slash command. */
export function slashQuery(draft: string): string | null {
  if (draft[0] !== "/" || /\s/.test(draft)) {
    return null;
  }
  return draft.slice(1);
}

/** Entries whose name contains the query (case-insensitive), capped for a compact menu. */
export function filterSlash(entries: readonly AgentSlashEntry[], query: string): AgentSlashEntry[] {
  const needle = query.toLowerCase();
  return entries.filter((entry) => entry.name.toLowerCase().includes(needle)).slice(0, 8);
}
