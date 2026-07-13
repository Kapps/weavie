import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";
import { hasItemId } from "./AgentPaneMessageFormat";

/**
 * Whether the pane's latest turn is still running (started with no completion yet). An interrupted turn
 * also ends with `turn-completed` — Codex reports it as status "interrupted", never as a separate type.
 */
export function hasActiveTurn(messages: readonly AgentPaneUpdate[]): boolean {
  let active = false;
  for (const message of messages) {
    if (!isPrimary(message)) {
      continue;
    }
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
    if (!isPrimary(message)) {
      continue;
    }
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

/**
 * The latest request whose itemId has no resolution yet — the same resolution-based signal that keeps the
 * card's buttons on screen. A turn boundary must NOT clear it: a request is answerable for exactly as long
 * as it is unresolved, so the hotkey chip and chord never drop off a card that still shows its buttons.
 */
export function pendingRequest(messages: readonly AgentPaneUpdate[]): PendingRequest | null {
  const pending = new Map<string, PendingRequest>();
  for (const message of messages) {
    if (message.type === "approval-requested" && hasItemId(message)) {
      const key = paneItemIdentity(message);
      if (key !== null) pending.set(key, { kind: "approval", requestId: message.itemId });
    } else if (message.type === "input-requested" && hasItemId(message)) {
      const key = paneItemIdentity(message);
      if (key !== null) pending.set(key, { kind: "input", requestId: message.itemId });
    } else if (
      (message.type === "approval-resolved" || message.type === "input-resolved") &&
      hasItemId(message)
    ) {
      const key = paneItemIdentity(message);
      if (key !== null) pending.delete(key);
    }
  }
  let latest: PendingRequest | null = null;
  for (const request of pending.values()) {
    latest = request;
  }
  return latest;
}

/**
 * The one approval the keyboard decision commands answer and the card chips advertise: the newest
 * unresolved approval. Derived from the same resolution state as the buttons, so the chip, the chord, and
 * the buttons agree — a card is keyboard-answerable for exactly as long as it is clickable.
 */
export function pendingApproval(messages: readonly AgentPaneUpdate[]): PendingRequest | null {
  const request = pendingRequest(messages);
  return request !== null && request.kind === "approval" ? request : null;
}

function isPrimary(message: AgentPaneUpdate): boolean {
  return message.isPrimaryThread !== false;
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
