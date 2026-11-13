import type { ClientSession } from "../bridge";
import { type AgentCardRef, cardForTab, type TabKind } from "../editor/tab-entry";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { agentPaneModel } from "./pane-store";

/** The live entry the card tab of `kind` at `path` shows, from its session's own transcript. */
export function cardForTabEntry(
  session: ClientSession | null,
  kind: TabKind,
  path: string,
): AgentTranscriptEntry | undefined {
  const card = cardForTab(kind, path);
  return card === null ? undefined : cardEntry(session, card);
}

/** The live transcript entry of `card`. */
export function cardEntry(
  session: ClientSession | null,
  card: AgentCardRef,
): AgentTranscriptEntry | undefined {
  const model = agentPaneModel(session);
  switch (card.kind) {
    case "subagent":
    case "aside":
      return model?.conversation(card.conversationId);
    case "dashboard":
      return model?.dashboard(card.conversationId, card.dashboardId);
    case "block":
      return model?.block(card.conversationId, card.blockId);
  }
}

/** `session`, once it holds `card`; throws for a card that is gone, so a command never acts on nothing. */
export function liveCard(session: ClientSession | null, card: AgentCardRef): ClientSession {
  if (session === null) throw new Error("Select a session to act on its cards.");
  if (cardEntry(session, card) === undefined)
    throw new Error("That card is no longer in this conversation.");
  return session;
}
