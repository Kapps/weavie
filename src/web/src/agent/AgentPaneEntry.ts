import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity, paneTurnIdentity } from "./AgentPaneIdentity";
import { normalizeStatus, normalizeText } from "./AgentPaneMessageFormat";
import type { AgentTranscriptEntry, AgentTranscriptTone } from "./AgentPaneTranscriptTypes";

/** A transcript entry for `message`, at its place `sequence` in the transcript. */
export function entry(
  message: AgentPaneUpdate,
  sequence: number,
  kind: AgentTranscriptEntry["kind"],
  tone: AgentTranscriptTone,
  label: string,
  status: string | null,
): AgentTranscriptEntry {
  return {
    actionMessage: actionMessage(message),
    detailCount: 0,
    details: [],
    id: paneItemIdentity(message) ?? `${message.type}-${sequence}`,
    turnKey: paneTurnIdentity(message) ?? undefined,
    kind,
    label,
    status,
    streaming: normalizeStatus(message.status) === "running",
    summary: normalizeText(message.summary),
    text: normalizeText(message.text),
    tone,
  };
}

function actionMessage(message: AgentPaneUpdate): AgentPaneUpdate | null {
  return message.type === "approval-requested" ||
    message.type === "authentication-requested" ||
    message.type === "edit-location" ||
    message.type === "input-requested" ||
    (message.mediaData !== null && message.mediaData !== undefined) ||
    (message.resourceUri !== null && message.resourceUri !== undefined) ||
    (message.content?.length ?? 0) > 0
    ? message
    : null;
}

/** `message`'s side conversation as an entry field; none for the main conversation. */
export const inConversation = (message: AgentPaneUpdate): { conversationId?: string } =>
  message.conversationId ? { conversationId: message.conversationId } : {};
