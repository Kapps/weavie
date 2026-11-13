import type { AgentPaneUpdate } from "../bridge";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";

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
  let terminalFailed = false;
  const activeItems = new Set<string>();
  const pendingRequests = new Set<string>();
  for (const message of messages) {
    if (message.type === "side-conversation-started") {
      markerActive = message.status === "forking";
    } else if (
      message.type === "side-conversation-failed" ||
      message.type === "side-conversation-closed"
    ) {
      terminal = true;
      terminalFailed = message.type === "side-conversation-failed";
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
    failed: terminalFailed || (!active && latestTurnFailed),
    replyable: !terminal && !active,
  };
}

export function sideConversationEntry(
  origin: AgentPaneUpdate,
  messages: readonly AgentPaneUpdate[],
  project: (messages: readonly AgentPaneUpdate[]) => AgentTranscriptEntry[],
): AgentTranscriptEntry {
  const first = origin;
  const marker = [...messages]
    .reverse()
    .find((message) => message.type === "side-conversation-started");
  const sideFailure = [...messages]
    .reverse()
    .find((message) => message.type === "side-conversation-failed");
  const collapse = [...messages]
    .reverse()
    .find((message) => message.type === "side-conversation-collapsed");
  const state = sideConversationState(messages);
  const childMessages = [...messages];
  if (!childMessages.some((message) => message.type === "user-message") && marker?.text) {
    childMessages.unshift({
      type: "user-message",
      providerId: marker.providerId,
      conversationId: first.conversationId ?? null,
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
    label: marker?.summary ?? "BTW",
    status: state.active ? "thinking" : state.failed ? "failed" : null,
    streaming: state.active,
    // The question that started it, which names it where its label alone can't tell BTWs apart.
    summary: marker?.text ?? null,
    text: null,
    tone: state.failed ? "error" : "assistant",
  };
}

export function subagentEntry(
  messages: readonly AgentPaneUpdate[],
  parentName: string | null,
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
    asideEntries: project(messages.filter((message) => message !== marker)),
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
      via: parentName,
    },
    summary: name,
    text: marker.text || null,
    tone: state === "failed" ? "error" : "assistant",
  };
}

const subagentCardTail = 4;

/** Cards preview recent entries until history is explicitly opened; collapsed cards retain requests. */
export function subagentCardEntries(
  entries: readonly AgentTranscriptEntry[],
  expanded: boolean,
): AgentTranscriptEntry[] {
  const historyOpen = entries.some((entry) => entry.kind === "updates" && entry.updatesOpen);
  return entries.filter(
    (entry, index) =>
      (expanded && (historyOpen || index >= entries.length - subagentCardTail)) ||
      entry.kind === "request",
  );
}
