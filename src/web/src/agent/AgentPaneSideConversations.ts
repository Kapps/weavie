import type { AgentPaneUpdate } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

export function groupSideConversations(messages: readonly AgentPaneUpdate[]): {
  conversations: ReadonlyMap<string, AgentPaneUpdate[]>;
  containers: ReadonlyMap<string, string>;
} {
  const sides = new Set<string>();
  const containers = new Map<string, string>();
  for (const message of messages) {
    if (!message.conversationId) continue;
    if (message.type === "side-conversation-started") sides.add(message.conversationId);
    if (message.type !== "subagent-started" || !message.parentItemId) continue;
    const container = sides.has(message.parentItemId)
      ? message.parentItemId
      : containers.get(message.parentItemId);
    if (container !== undefined) containers.set(message.conversationId, container);
  }
  return { conversations: collectConversations(messages, containers), containers };
}

export function collectSideConversations(
  messages: readonly AgentPaneUpdate[],
): ReadonlyMap<string, AgentPaneUpdate[]> {
  return collectConversations(messages, new Map());
}

function collectConversations(
  messages: readonly AgentPaneUpdate[],
  containers: ReadonlyMap<string, string>,
): ReadonlyMap<string, AgentPaneUpdate[]> {
  const conversations = new Map<string, AgentPaneUpdate[]>();
  for (const message of messages) {
    if (!message.conversationId) continue;
    const id = containers.get(message.conversationId) ?? message.conversationId;
    const existing = conversations.get(id);
    if (existing === undefined) conversations.set(id, [message]);
    else existing.push(message);
  }
  return conversations;
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
  const own = messages.filter((message) => message.conversationId === first.conversationId);
  const marker = [...own].reverse().find((message) => message.type === "side-conversation-started");
  const sideFailure = [...own]
    .reverse()
    .find((message) => message.type === "side-conversation-failed");
  const collapse = [...own]
    .reverse()
    .find((message) => message.type === "side-conversation-collapsed");
  const state = sideConversationState(own);
  const childMessages: AgentPaneUpdate[] = messages
    .filter((message) => !message.type.startsWith("side-conversation-"))
    .map((message) =>
      message.conversationId === first.conversationId
        ? { ...message, conversationId: null, anchorTurnId: null }
        : message,
    );
  if (!own.some((message) => message.type === "user-message") && marker?.text) {
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
  if (sideFailure !== undefined && !own.some((message) => message.type === "error")) {
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
    asideCollapsed: collapse?.status === "collapsed",
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

/** Whether a nested conversation is a subagent rather than a BTW. */
export function isSubagentConversation(messages: readonly AgentPaneUpdate[]): boolean {
  return messages.some(
    (message) =>
      message.type === "subagent-started" && message.conversationId === messages[0]!.conversationId,
  );
}

/** The subagent name each nested conversation announced, for naming a nested spawn's parent. */
export function subagentNames(
  conversations: ReadonlyMap<string, readonly AgentPaneUpdate[]>,
): ReadonlyMap<string, string> {
  const names = new Map<string, string>();
  for (const [id, messages] of conversations) {
    const marker = messages.find(
      (message) => message.type === "subagent-started" && message.conversationId === id,
    );
    if (marker?.summary) names.set(id, marker.summary);
  }
  return names;
}

export function subagentEntry(
  messages: readonly AgentPaneUpdate[],
  names: ReadonlyMap<string, string>,
  project: (messages: readonly AgentPaneUpdate[]) => AgentTranscriptEntry[],
): AgentTranscriptEntry {
  const marker = messages.find((message) => message.type === "subagent-started")!;
  const completion = [...messages].reverse().find((message) => message.type === "turn-completed");
  const state = completion?.status ?? "running";
  const running = completion === undefined;
  const name = marker.summary ?? "Subagent";
  return {
    actionMessage: null,
    asideActive: running,
    asideEntries: project(
      messages
        .filter((message) => message !== marker)
        .map((message) => ({ ...message, conversationId: null, anchorTurnId: null })),
    ),
    conversationId: marker.conversationId!,
    detailCount: 0,
    details: [],
    id: `subagent-${marker.conversationId}`,
    kind: "subagent",
    label: "Subagent",
    status: state,
    streaming: running,
    subagent: {
      completedAtMs: completion?.completedAtMs ?? null,
      name,
      startedAtMs: marker.startedAtMs ?? null,
      state,
      task: marker.text || null,
      via: marker.parentItemId ? (names.get(marker.parentItemId) ?? null) : null,
    },
    summary: name,
    text: marker.text || null,
    tone: state === "failed" ? "error" : "assistant",
  };
}

const subagentCardTail = 4;

/** A subagent card shows its latest entries and every request; Open shows the whole transcript. */
export function subagentCardEntries(
  entries: readonly AgentTranscriptEntry[],
): AgentTranscriptEntry[] {
  return entries.filter(
    (entry, index) => index >= entries.length - subagentCardTail || entry.kind === "request",
  );
}
