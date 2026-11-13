import type { AgentPaneUpdate } from "../bridge";
import { blockKey, paneItemIdentity } from "./AgentPaneIdentity";
import { normalizeText } from "./AgentPaneMessageFormat";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

const dashboardPrefix = "dashboard:";

/** Whether `entry` is a card a user can open and collapse. */
export const isCard = (entry: AgentTranscriptEntry): boolean =>
  entry.kind === "aside" || entry.kind === "dashboard";

/** The transcript entry id of dashboard `dashboardId` in one conversation. */
export const dashboardEntryId = (conversationId: string | null, dashboardId: string): string =>
  `dashboard-${dashboardId}@${conversationId ?? ""}`;

type Card = AgentTranscriptEntry & { asideEntries: AgentTranscriptEntry[] };

/**
 * Projects a conversation's block rows: a loose block is its own entry; a dashboard's row is its card, which holds
 * every block row that names it, wherever that row sits. A block added in a later turn also leaves a line there.
 */
export class AgentPaneBlocks {
  private readonly cards = new Map<string, Card>();
  private readonly waiting = new Map<string, AgentTranscriptEntry[]>();

  /** The entries `message` adds at its place, or null when it is not a block or dashboard row. */
  take(message: AgentPaneUpdate, sequence: number): AgentTranscriptEntry[] | null {
    if (message.itemType === "dashboard" && message.itemId) {
      if (message.type === "dashboard-collapsed") {
        const card = this.cards.get(message.itemId);
        if (card !== undefined) card.asideCollapsed = message.status === "collapsed";
        return [];
      }
      if (message.type !== "item-completed") return [];
      const card = dashboardCard(message, this.waiting.get(message.itemId) ?? []);
      this.waiting.delete(message.itemId);
      this.cards.set(message.itemId, card);
      return [card];
    }
    if (message.type !== "item-completed" || message.itemType !== "block" || !message.blockId) {
      return null;
    }
    const block = blockEntry(message, sequence);
    const parent = message.parentItemId;
    if (!parent?.startsWith(dashboardPrefix)) return [block];
    const card = this.cards.get(parent);
    if (card !== undefined) card.asideEntries.push(block);
    else this.waiting.set(parent, [...(this.waiting.get(parent) ?? []), block]);
    const line = normalizeText(message.text);
    return line === null
      ? []
      : [addedEntry(message, block, parent.slice(dashboardPrefix.length), line)];
  }
}

function blockEntry(message: AgentPaneUpdate, sequence: number): AgentTranscriptEntry {
  const kind = message.category === "table" ? "table" : "chart";
  return {
    ...bare(paneItemIdentity(message) ?? `block-${sequence}`, normalizeText(message.summary)),
    kind: "block",
    label: kind === "table" ? "Table" : "Chart",
    ...(message.conversationId ? { conversationId: message.conversationId } : {}),
    block: {
      id: message.blockId!,
      key: blockKey(message.providerId, message.conversationId, message.blockId!),
      kind,
    },
  };
}

function dashboardCard(message: AgentPaneUpdate, members: AgentTranscriptEntry[]): Card {
  const dashboardId = message.itemId!.slice(dashboardPrefix.length);
  return {
    ...bare(
      dashboardEntryId(message.conversationId ?? null, dashboardId),
      normalizeText(message.summary),
    ),
    kind: "dashboard",
    label: "Dashboard",
    asideEntries: members,
    asideCollapsed: false,
    dashboardId,
    ...(message.conversationId ? { conversationId: message.conversationId } : {}),
  };
}

function addedEntry(
  message: AgentPaneUpdate,
  block: AgentTranscriptEntry,
  dashboardId: string,
  line: string,
): AgentTranscriptEntry {
  return {
    ...bare(`${block.id}:added`, `${block.summary ?? block.label} · ${line}`),
    kind: "block-added",
    label: block.label,
    dashboardId,
    ...(message.conversationId ? { conversationId: message.conversationId } : {}),
  };
}

function bare(id: string, summary: string | null): Omit<AgentTranscriptEntry, "kind" | "label"> {
  return {
    actionMessage: null,
    detailCount: 0,
    details: [],
    id,
    status: null,
    streaming: false,
    summary,
    text: null,
    tone: "assistant",
  };
}
