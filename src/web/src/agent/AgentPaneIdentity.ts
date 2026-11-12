import type { AgentPaneUpdate } from "../bridge";

export function paneItemIdentity(message: AgentPaneUpdate): string | null {
  return message.itemId === null || message.itemId === undefined || message.itemId.length === 0
    ? null
    : JSON.stringify([
        message.providerId,
        message.conversationId ?? null,
        message.threadId ?? null,
        message.turnId ?? null,
        message.itemId,
      ]);
}

export function paneTurnIdentity(message: AgentPaneUpdate): string | null {
  return message.turnId === null || message.turnId === undefined || message.turnId.length === 0
    ? null
    : JSON.stringify([
        message.providerId,
        message.conversationId ?? null,
        message.threadId ?? null,
        message.turnId,
      ]);
}

export function paneActivityIdentity(message: AgentPaneUpdate, fallbackTurnId: string): string {
  return JSON.stringify([
    message.providerId,
    message.conversationId ?? null,
    message.threadId ?? null,
    message.turnId ?? fallbackTurnId,
  ]);
}

/** The key of block `blockId` in one provider conversation. */
export function blockKey(
  providerId: string,
  conversationId: string | null | undefined,
  blockId: string,
): string {
  return `${providerId}\u0000${conversationId ?? ""}\u0000${blockId}`;
}
