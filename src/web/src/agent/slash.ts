import type { AgentControlState, AgentSlashEntry } from "../bridge";
import { type CommandUsage, rankCommands } from "../commands/ranking";
import { CommandIds } from "../commands/types";

/** The command input and entries at the selected conversation destination. */
export function slashCompletionScope(entries: readonly AgentSlashEntry[], draft: string) {
  const command = weavieCommandForDraft(entries, draft);
  const prefix =
    command?.commandId === CommandIds.askAgentAside ? (/^\s*\/\S+\s+/.exec(draft)?.[0] ?? "") : "";
  return {
    prefix,
    draft: draft.slice(prefix.length),
    entries:
      prefix.length > 0 ? entries.filter((entry) => entry.kind !== "weavieCommand") : entries,
  };
}

/** The query after the leading slash, or null when the draft isn't a slash command. */
export function slashQuery(draft: string): string | null {
  if (draft[0] !== "/" || /\s/.test(draft)) {
    return null;
  }
  return draft.slice(1);
}

/** All matching slash commands remain keyboard-reachable in the scrolling menu. */
export function filterSlash(entries: readonly AgentSlashEntry[], query: string): AgentSlashEntry[] {
  return rankSlash(entries, query, [], (entry) => entry.id);
}

export function rankSlash(
  entries: readonly AgentSlashEntry[],
  query: string,
  usage: readonly CommandUsage[],
  identity: (entry: AgentSlashEntry) => string,
): AgentSlashEntry[] {
  return rankCommands(
    entries.map((entry) => ({
      item: entry,
      id: identity(entry),
      title: entry.name,
      aliases: [],
      metadata: entry.description,
    })),
    query,
    usage,
  ).map((row) => row.item);
}

/** The exact provider command or bundled MCP prompt invoked by a draft. */
export function agentInvocationForDraft(
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
        (entry.kind === "providerCommand" || entry.kind === "mcpPrompt") &&
        entry.name.toLowerCase() === name.toLowerCase(),
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

export type AgentDraft =
  | { kind: "prompt" | "loading" }
  | { kind: "command"; entry: AgentSlashEntry };

/** The agent-owned invocation shared by primary and forked submissions. */
export function invocationForAction(action: AgentDraft) {
  return action.kind === "command" && action.entry.kind !== "weavieCommand"
    ? { kind: action.entry.kind, name: action.entry.name }
    : null;
}

/** Resolves slash input only after its provider catalog is known; local actions remain available. */
export function classifyAgentDraft(state: AgentControlState, draft: string): AgentDraft {
  const local = weavieCommandForDraft(state.slash, draft);
  if (local !== null) return { kind: "command", entry: local };
  if (!state.ready && draft.trimStart().startsWith("/")) return { kind: "loading" };
  const provider = agentInvocationForDraft(state.slash, draft);
  return provider === null ? { kind: "prompt" } : { kind: "command", entry: provider };
}

/** Resolves the submitted input after an optional side-conversation destination. */
export function classifyAgentSubmission(state: AgentControlState, draft: string): AgentDraft {
  const action = classifyAgentDraft(state, draft);
  return action.kind === "command" && action.entry.commandId === CommandIds.askAgentAside
    ? classifyAgentDraft(state, weavieCommandInput(action.entry, draft) ?? "")
    : action;
}
