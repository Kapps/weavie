import type { AgentPaneUpdate } from "../bridge";
import { hasItemId } from "./AgentPaneMessageFormat";

/**
 * Whether the pane's latest turn is still running (started with no completion yet). An interrupted turn
 * also ends with `turn-completed` — Codex reports it as status "interrupted", never as a separate type.
 */
export function hasActiveTurn(messages: readonly AgentPaneUpdate[]): boolean {
  let active = false;
  for (const message of messages) {
    if (message.type === "turn-started") {
      active = true;
    } else if (message.type === "turn-completed") {
      active = false;
    }
  }
  return active;
}

/**
 * Wall-clock ms the running turn began (the arrival time stamped on its `turn-started`), or null when no
 * turn is active. Derived from the message stream so it stays fixed across session switches and re-mounts.
 */
export function activeTurnStartedAt(messages: readonly AgentPaneUpdate[]): number | null {
  let startedAt: number | null = null;
  for (const message of messages) {
    if (message.type === "turn-started") {
      startedAt = message.receivedAt ?? null;
    } else if (message.type === "turn-completed") {
      startedAt = null;
    }
  }
  return startedAt;
}

export type PendingRequestKind = "approval" | "input";

export interface PendingRequest {
  kind: PendingRequestKind;
  requestId: string;
}

/** The latest unresolved approval/input request — the turn is blocked on the user, not working. */
export function pendingRequest(messages: readonly AgentPaneUpdate[]): PendingRequest | null {
  const pending = new Map<string, PendingRequestKind>();
  for (const message of messages) {
    if (message.type === "turn-started" || message.type === "turn-completed") {
      pending.clear();
    } else if (message.type === "approval-requested" && hasItemId(message)) {
      pending.set(message.itemId, "approval");
    } else if (message.type === "input-requested" && hasItemId(message)) {
      pending.set(message.itemId, "input");
    } else if (
      (message.type === "approval-resolved" || message.type === "input-resolved") &&
      hasItemId(message)
    ) {
      pending.delete(message.itemId);
    }
  }
  let latest: PendingRequest | null = null;
  for (const [requestId, kind] of pending) {
    latest = { kind, requestId };
  }
  return latest;
}

/**
 * The one approval the keyboard decision commands answer: the newest pending request of an active
 * turn, when it is an approval. The chips and the commands must derive this identically.
 */
export function pendingApproval(messages: readonly AgentPaneUpdate[]): PendingRequest | null {
  if (!hasActiveTurn(messages)) {
    return null;
  }
  const request = pendingRequest(messages);
  return request !== null && request.kind === "approval" ? request : null;
}

/** Elapsed working time as a compact label: "8s", "1m 05s", "1h 02m". */
export function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const seconds = total % 60;
  const minutes = Math.floor(total / 60) % 60;
  const hours = Math.floor(total / 3600);
  if (hours > 0) {
    return `${hours}h ${String(minutes).padStart(2, "0")}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${String(seconds).padStart(2, "0")}s`;
  }
  return `${seconds}s`;
}
