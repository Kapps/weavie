import { onCleanup } from "solid-js";
import type { ClientSession } from "../bridge";
import { runCommandWithFeedback } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { AgentCardRef } from "../editor/tab-entry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { ownsCommand, ownTarget } from "./agent-command-owner";

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

/** Opens `card` in an editor tab of its session. */
export const openCard = (card: AgentCardRef): Promise<unknown> =>
  runCommandWithFeedback(CommandIds.openAgentCard, card);

/** Records a BTW or dashboard card collapsed or expanded, in the session's history. */
export function setCardCollapsed(
  session: ClientSession,
  card: { conversationId: string | null; dashboardId: string | null },
  collapsed: boolean,
): void {
  session.feature("agent").publish("setCardCollapsed", { ...card, collapsed });
}

/** What the card commands act on when no card is named: the card the user last pressed or focused. */
export interface OwnedCard {
  session: ClientSession;
  card: AgentCardRef;
  /** Collapses or expands the card; null for one that has no collapsed state, a lone block. */
  toggle: (() => void) | null;
}

export const cardFocusContext = "agentCardFocused";

/** Makes the card the user presses or focuses the one the card commands act on, until they move on. */
export function ownCardOnFocus(target: () => OwnedCard | null): (event: Event) => void {
  let release: (() => void) | null = null;
  onCleanup(() => release?.());
  return (event) => {
    const owned = target();
    if (owned === null || ownsCommand(cardFocusContext, release)) return;
    release = ownTarget(cardFocusContext, event.currentTarget as Element, owned);
  };
}

/** What a card is called where its transcript isn't around it: in the strip and on its tab. */
export function cardName(entry: AgentTranscriptEntry): string {
  if (entry.kind === "subagent") return `Subagent · ${entry.subagent?.name ?? entry.summary}`;
  if (entry.kind === "aside" && entry.summary !== null)
    return `${entry.label} · ${entry.summary.split("\n")[0]}`;
  return entry.summary ?? entry.label;
}
