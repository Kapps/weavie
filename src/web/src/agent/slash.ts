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

/** The exact currently-advertised provider command invoked by a draft, including drafts with arguments. */
export function providerCommandForDraft(
  entries: readonly AgentSlashEntry[],
  draft: string,
): AgentSlashEntry | null {
  const text = draft.trim();
  if (!text.startsWith("/")) return null;
  const boundary = text.search(/\s/);
  const name = text.slice(1, boundary < 0 ? undefined : boundary);
  return (
    entries.find(
      (entry) =>
        entry.kind === "providerCommand" && entry.name.toLowerCase() === name.toLowerCase(),
    ) ?? null
  );
}

/** The exact Weavie slash action invoked by a draft, including its required free-form input when any. */
export function weavieCommandForDraft(
  entries: readonly AgentSlashEntry[],
  draft: string,
): AgentSlashEntry | null {
  const text = draft.trim();
  if (!text.startsWith("/")) return null;
  const boundary = text.search(/\s/);
  const name = text.slice(1, boundary < 0 ? undefined : boundary);
  const entry =
    entries.find(
      (candidate) =>
        candidate.kind === "weavieCommand" && candidate.name.toLowerCase() === name.toLowerCase(),
    ) ?? null;
  if (entry === null) return null;
  const input = boundary < 0 ? "" : text.slice(boundary).trim();
  return entry.inputName === null && input.length > 0 ? null : entry;
}

/** The free-form input following one exact Weavie slash command. */
export function weavieCommandInput(entry: AgentSlashEntry, draft: string): string | null {
  if (entry.kind !== "weavieCommand" || entry.inputName === null) return null;
  const text = draft.trim();
  const boundary = text.search(/\s/);
  return boundary < 0 ? null : text.slice(boundary).trim() || null;
}
