import type { AgentPaneUpdate } from "../bridge";
import { entry, inConversation } from "./AgentPaneEntry";
import { blockKey } from "./AgentPaneIdentity";
import { normalizeText } from "./AgentPaneMessageFormat";
import type { AgentBlockTranscriptEntry, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

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
      const card = dashboardCard(message, sequence, this.waiting.get(message.itemId) ?? []);
      this.waiting.delete(message.itemId);
      this.cards.set(message.itemId, card);
      return [card];
    }
    if (message.type !== "item-completed" || message.itemType !== "block" || !message.blockId) {
      return null;
    }
    const parent = message.parentItemId?.startsWith(dashboardPrefix) ? message.parentItemId : null;
    const block = blockEntry(message, sequence, parent?.slice(dashboardPrefix.length) ?? null);
    if (parent === null) return [block];
    const card = this.cards.get(parent);
    if (card !== undefined) card.asideEntries.push(block);
    else this.waiting.set(parent, [...(this.waiting.get(parent) ?? []), block]);
    const line = normalizeText(message.text);
    return line === null ? [] : [addedEntry(message, sequence, block, line)];
  }
}

function blockEntry(
  message: AgentPaneUpdate,
  sequence: number,
  dashboardId: string | null,
): AgentBlockTranscriptEntry {
  const kind = message.category === "table" ? "table" : "chart";
  return {
    ...entry(message, sequence, "block", "assistant", kind === "table" ? "Table" : "Chart", null),
    ...inConversation(message),
    ...(dashboardId === null ? {} : { dashboardId }),
    // A member's text is the line its turn shows, never the block's.
    text: null,
    block: {
      id: message.blockId!,
      key: blockKey(message.providerId, message.conversationId, message.blockId!),
      kind,
    },
  };
}

function dashboardCard(
  message: AgentPaneUpdate,
  sequence: number,
  members: AgentTranscriptEntry[],
): Card {
  const dashboardId = message.itemId!.slice(dashboardPrefix.length);
  return {
    ...entry(message, sequence, "dashboard", "assistant", "Dashboard", null),
    ...inConversation(message),
    id: dashboardEntryId(message.conversationId ?? null, dashboardId),
    asideEntries: members,
    asideCollapsed: false,
    dashboardId,
  };
}

function addedEntry(
  message: AgentPaneUpdate,
  sequence: number,
  block: AgentBlockTranscriptEntry,
  line: string,
): AgentTranscriptEntry {
  return {
    ...entry(message, sequence, "block-added", "assistant", block.label, null),
    ...inConversation(message),
    id: `${block.id}:added`,
    summary: `${block.summary ?? block.label} · ${line}`,
    text: null,
    dashboardId: block.dashboardId!,
  };
}
