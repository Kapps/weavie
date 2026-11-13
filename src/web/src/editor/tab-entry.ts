import { normalizePath, samePath } from "./fs-path";
import type { EditorSessionEntry } from "./session-types";

export const REVIEW_TAB_KEY = "weavie:review";
export type TabKind = NonNullable<EditorSessionEntry["kind"]>;
export const tabKind = (entry: Pick<EditorSessionEntry, "kind">): TabKind => entry.kind ?? "file";
export const isFileTab = (entry: Pick<EditorSessionEntry, "kind">): boolean =>
  tabKind(entry) === "file";
export const matchesTab = (entry: EditorSessionEntry, path: string): boolean =>
  isFileTab(entry) ? samePath(entry.path, path) : entry.path === path;
export const tabResourceKey = (entry: EditorSessionEntry): string =>
  `${tabKind(entry)}\0${isFileTab(entry) ? normalizePath(entry.path) : entry.path}`;

/** A card a tab can show: a nested conversation, a dashboard, or a lone block, in its own conversation. */
export type AgentCardRef =
  | { kind: "subagent" | "aside"; conversationId: string }
  | { kind: "dashboard"; conversationId: string | null; dashboardId: string }
  | { kind: "block"; conversationId: string | null; blockId: string };

/** The editor tab path of `card`; a dashboard's matches the host's `AgentDashboards.TabPath`. */
export function cardTabPath(card: AgentCardRef): string {
  switch (card.kind) {
    case "subagent":
    case "aside":
      return `${card.kind}:${card.conversationId}`;
    case "dashboard":
      return `dashboard:${card.dashboardId}@${card.conversationId ?? ""}`;
    case "block":
      return `block:${card.blockId}@${card.conversationId ?? ""}`;
  }
}

/** The card a tab of `kind` at `path` shows, or null for any other tab. */
export function cardForTab(kind: TabKind, path: string): AgentCardRef | null {
  const rest = path.slice(kind.length + 1);
  if (kind === "subagent" || kind === "aside") return { kind, conversationId: rest };
  if (kind !== "dashboard" && kind !== "block") return null;
  // An id never holds '@', so the first one ends it.
  const at = rest.indexOf("@");
  const id = rest.slice(0, at);
  const conversationId = rest.slice(at + 1) || null;
  return kind === "dashboard"
    ? { kind, conversationId, dashboardId: id }
    : { kind, conversationId, blockId: id };
}
