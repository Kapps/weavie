import { cardTabPath } from "../editor/tab-entry";
import type { AgentBackgroundItem } from "./agent-background-store";

/** The item `id` names; throws when it names none. */
export function backgroundItemToOpen(
  items: readonly AgentBackgroundItem[],
  id: unknown,
): AgentBackgroundItem {
  if (typeof id !== "string") throw new Error("A background item id must be a string.");
  const item = items.find((candidate) => candidate.id === id);
  if (item === undefined) throw new Error("That background work is no longer listed.");
  return item;
}

/** The editor tab that shows `item`: a subagent's transcript, or a task's own tab keyed by its item id. */
export function backgroundTab(item: AgentBackgroundItem): {
  kind: "subagent" | "task";
  path: string;
} {
  return item.kind === "subagent"
    ? { kind: "subagent", path: cardTabPath({ kind: "subagent", conversationId: item.id }) }
    : { kind: "task", path: item.id };
}
