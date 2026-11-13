import { onCleanup } from "solid-js";
import type { ClientSession } from "../bridge";
import { liveKeyHint } from "../commands/keys-live";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { AgentCardRef } from "../editor/tab-entry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { ownsCommand, ownTarget } from "./agent-command-owner";

/** A card with a collapsed state, kept in its conversation's history. */
export type CollapsibleCard = Extract<AgentCardRef, { kind: "aside" | "dashboard" }>;

/** The card `entry` is, for opening its tab; null for an entry that is not one. */
export function cardOf(entry: AgentTranscriptEntry): AgentCardRef | null {
  const conversationId = entry.conversationId ?? null;
  if (entry.kind === "dashboard")
    return { kind: "dashboard", conversationId, dashboardId: entry.dashboardId! };
  if (entry.block !== undefined) return { kind: "block", conversationId, blockId: entry.block.id };
  if ((entry.kind === "aside" || entry.kind === "subagent") && conversationId !== null)
    return { kind: entry.kind, conversationId };
  return null;
}

/** The card a command's `args` name; null when they name none, and throws when they name one badly. */
export function namedCard(args: unknown): AgentCardRef | null {
  if (typeof args !== "object" || args === null) return null;
  const { kind, conversationId = null, dashboardId, blockId } = args as Record<string, unknown>;
  if (
    kind === undefined &&
    conversationId === null &&
    dashboardId === undefined &&
    blockId === undefined
  )
    return null;
  if (conversationId !== null && typeof conversationId !== "string")
    throw new Error("A card's conversationId must be a string, or null for the main conversation.");
  if ((kind === "subagent" || kind === "aside") && conversationId !== null)
    return { kind, conversationId };
  if (kind === "dashboard" && typeof dashboardId === "string")
    return { kind, conversationId, dashboardId };
  if (kind === "block" && typeof blockId === "string") return { kind, conversationId, blockId };
  throw new Error(
    "Name a card by kind: subagent or aside with its conversationId, dashboard with its dashboardId, block with its blockId.",
  );
}

/** Opens `card` in an editor tab of its session. */
export const openCard = (card: AgentCardRef): Promise<unknown> =>
  runCommandWithFeedback(CommandIds.openAgentCard, card);

/** The tooltip of every control that opens a card in a tab. */
export const openCardTitle = (): string =>
  `Open Card in Tab${liveKeyHint(CommandIds.openAgentCard)}`;

/** Records `card` collapsed or expanded, in the session's history. */
export function setCardCollapsed(
  session: ClientSession,
  card: CollapsibleCard,
  collapsed: boolean,
): void {
  session.feature("agent").publish("setCardCollapsed", {
    conversationId: card.conversationId,
    dashboardId: card.kind === "dashboard" ? card.dashboardId : null,
    collapsed,
  });
}

/** What a card is called where its transcript isn't around it: in the strip and on its tab. */
export function cardName(entry: AgentTranscriptEntry): string {
  if (entry.kind === "subagent") return `Subagent · ${entry.subagent?.name ?? entry.summary}`;
  if (entry.kind === "aside" && entry.summary !== null)
    return `${entry.label} · ${entry.summary.split("\n")[0]}`;
  return entry.summary ?? entry.label;
}

/** What the card commands act on when no card is named: the card the user last pressed or focused. */
export interface OwnedCard {
  session: ClientSession;
  card: AgentCardRef;
  /** Collapses or expands the card; null for one that has no collapsed state, a lone block. */
  toggle: (() => void) | null;
}

export const cardFocusContext = "agentCardFocused";

// A press inside a card inside a card reaches the inner one first, which takes it; the outer one leaves it.
const claimed = new WeakSet<Event>();

/** Makes the card the user presses or focuses the one the card commands act on, until they move on. */
export function ownCardOnFocus(target: () => OwnedCard | null): (event: Event) => void {
  let release: (() => void) | null = null;
  onCleanup(() => release?.());
  return (event) => {
    if (claimed.has(event)) return;
    const owned = target();
    if (owned === null) return;
    claimed.add(event);
    if (ownsCommand(cardFocusContext, release)) return;
    release = ownTarget(cardFocusContext, event.currentTarget as Element, owned);
  };
}

/** "updated 14:32": when a block, or a dashboard's newest block, last changed. */
export const blockUpdatedAt = (ms: number): string =>
  `updated ${new Date(ms).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
