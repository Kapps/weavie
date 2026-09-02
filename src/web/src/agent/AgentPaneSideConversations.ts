import type { AgentPaneUpdate } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function collectSideConversations(
  messages: readonly AgentPaneUpdate[],
): ReadonlyMap<string, AgentPaneUpdate[]> {
  const conversations = new Map<string, AgentPaneUpdate[]>();
  for (const message of messages) {
    if (!message.conversationId) continue;
    const existing = conversations.get(message.conversationId);
    if (existing === undefined) conversations.set(message.conversationId, [message]);
    else existing.push(message);
  }
  return conversations;
}

export function orderSideConversations(messages: readonly AgentPaneUpdate[]): AgentPaneUpdate[] {
  const primary = messages.filter((message) => !message.conversationId);
  const side = collectSideConversations(messages);
  if (side.size === 0) return [...messages];
  const byAnchor = new Map<string, AgentPaneUpdate[][]>();
  const unanchored: AgentPaneUpdate[][] = [];
  for (const conversation of side.values()) {
    const anchor = conversation[0]?.anchorTurnId;
    if (!anchor) {
      unanchored.push(conversation);
      continue;
    }
    const group = byAnchor.get(anchor);
    if (group === undefined) byAnchor.set(anchor, [conversation]);
    else group.push(conversation);
  }
  const lastIndexByTurn = new Map<string, number>();
  for (let index = 0; index < primary.length; index += 1) {
    const turnId = primary[index]?.turnId;
    if (turnId) lastIndexByTurn.set(turnId, index);
  }
  const result: AgentPaneUpdate[] = [];
  for (let index = 0; index < primary.length; index += 1) {
    const message = primary[index]!;
    result.push(message);
    if (message.turnId && lastIndexByTurn.get(message.turnId) === index) {
      for (const conversation of byAnchor.get(message.turnId) ?? []) result.push(...conversation);
      byAnchor.delete(message.turnId);
    }
  }
  for (const conversations of byAnchor.values()) {
    for (const conversation of conversations) result.push(...conversation);
  }
  for (const conversation of unanchored) result.push(...conversation);
  return result;
}

export interface SideConversationState {
  active: boolean;
  failed: boolean;
  replyable: boolean;
}

export function sideConversationState(messages: readonly AgentPaneUpdate[]): SideConversationState {
  let markerActive = false;
  let foregroundActive = false;
  let latestTurnFailed = false;
  let sawTurn = false;
  let terminal = false;
  const activeItems = new Set<string>();
  const pendingRequests = new Set<string>();
  for (const message of messages) {
    if (message.type === "side-conversation-started") {
      markerActive = message.status === "forking";
    } else if (message.type === "side-conversation-failed") {
      terminal = true;
    } else if (message.type === "turn-started") {
      foregroundActive = true;
      latestTurnFailed = false;
      sawTurn = true;
    } else if (message.type === "turn-completed") {
      foregroundActive = false;
      latestTurnFailed = message.status === "failed" || message.status === "refusal";
      sawTurn = true;
    }
    const itemKey =
      message.itemId === null || message.itemId === undefined
        ? null
        : `${message.threadId ?? ""}\0${message.turnId ?? ""}\0${message.itemId}`;
    if (message.type === "item-started" && itemKey !== null) activeItems.add(itemKey);
    else if (
      (message.type === "item-completed" || message.type === "item-retracted") &&
      itemKey !== null
    ) {
      activeItems.delete(itemKey);
    }
    if (message.requestId) {
      if (message.type.endsWith("-requested")) pendingRequests.add(message.requestId);
      else if (message.type.endsWith("-resolved")) pendingRequests.delete(message.requestId);
    }
  }
  const active =
    !terminal &&
    ((!sawTurn && markerActive) ||
      foregroundActive ||
      activeItems.size > 0 ||
      pendingRequests.size > 0);
  return {
    active,
    failed: terminal || (!active && latestTurnFailed),
    replyable: !terminal && !active,
  };
}

export function sideConversationEntry(
  messages: readonly AgentPaneUpdate[],
  project: (messages: readonly AgentPaneUpdate[]) => AgentTranscriptEntry[],
): AgentTranscriptEntry {
  const first = messages[0]!;
  const marker = [...messages]
    .reverse()
    .find((message) => message.type === "side-conversation-started");
  const sideFailure = [...messages]
    .reverse()
    .find((message) => message.type === "side-conversation-failed");
  const state = sideConversationState(messages);
  const childMessages: AgentPaneUpdate[] = messages
    .filter(
      (message) =>
        message.type !== "side-conversation-started" && message.type !== "side-conversation-failed",
    )
    .map((message) => ({
      ...message,
      conversationId: null,
      anchorTurnId: null,
    }));
  if (!childMessages.some((message) => message.type === "user-message") && marker?.text) {
    childMessages.unshift({
      type: "user-message",
      providerId: marker.providerId,
      threadId: first.conversationId ?? null,
      turnId: "1",
      itemId: `btw-question:${first.conversationId}`,
      itemType: "userMessage",
      text: marker.text,
    });
  }
  if (sideFailure !== undefined && !childMessages.some((message) => message.type === "error")) {
    childMessages.push({
      ...sideFailure,
      type: "error",
      conversationId: null,
      anchorTurnId: null,
      text: sideFailure.summary ?? null,
    });
  }
  return {
    actionMessage: null,
    asideActive: state.active,
    asideEntries: project(childMessages),
    asideReplyable: state.replyable,
    conversationId: first.conversationId!,
    detailCount: 0,
    details: [],
    id: `aside-${first.conversationId}`,
    kind: "aside",
    label: "BTW",
    status: state.active ? "thinking" : state.failed ? "failed" : null,
    streaming: state.active,
    summary: null,
    text: null,
    tone: state.failed ? "error" : "assistant",
  };
}
