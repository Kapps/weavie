import { subagentTabPath } from "../editor/tab-entry";
import type { AgentBackgroundItem } from "./agent-background-store";

/** The item `id` names, else the most recently started one; throws when there is none to open. */
export function backgroundItemToOpen(
  items: readonly AgentBackgroundItem[],
  id: unknown,
): AgentBackgroundItem {
  if (id !== undefined) {
    if (typeof id !== "string") throw new Error("A background item id must be a string.");
    const item = items.find((candidate) => candidate.id === id);
    if (item === undefined) throw new Error("That background work is no longer listed.");
    return item;
  }
  const latest = items.reduce<AgentBackgroundItem | undefined>(
    (best, item) => (best === undefined || item.startedAtMs >= best.startedAtMs ? item : best),
    undefined,
  );
  if (latest === undefined) throw new Error("This agent has no background work to open.");
  return latest;
}

/** The editor tab that shows `item`: a subagent's transcript, or a task's own tab keyed by its item id. */
export function backgroundTab(item: AgentBackgroundItem): {
  kind: "subagent" | "task";
  path: string;
} {
  return item.kind === "subagent"
    ? { kind: "subagent", path: subagentTabPath(item.id) }
    : { kind: "task", path: item.id };
}
