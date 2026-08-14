import type { AgentPaneUpdate } from "../bridge";
import { type RequestKind, requestLifecycles } from "./AgentPaneMessageFormat";

/**
 * Whether the pane's latest turn is still running (started with no completion yet). An interrupted turn
 * also ends with `turn-completed` using status "interrupted", never as a separate type.
 */
export function hasActiveTurn(messages: readonly AgentPaneUpdate[]): boolean {
  const progress = turnProgress(messages);
  return (
    progress.primaryActive || progress.activeTools.size > 0 || progress.providerActive.size > 0
  );
}

/**
 * Provider-recorded Unix ms when the running turn began, or null when the provider did not supply one.
 * Derived from the message stream so reconnecting and replaying cannot reset the elapsed clock.
 */
export function activeTurnStartedAt(messages: readonly AgentPaneUpdate[]): number | null {
  const progress = turnProgress(messages);
  const timestamps = [progress.primaryStartedAt, ...progress.activeTools.values()].filter(
    (value): value is number => value !== null,
  );
  return timestamps.length === 0 ? null : Math.min(...timestamps);
}

interface TurnProgress {
  activeTools: Map<string, number | null>;
  primaryActive: boolean;
  primaryStartedAt: number | null;
  providerActive: Set<string>;
}

function turnProgress(messages: readonly AgentPaneUpdate[]): TurnProgress {
  let primaryActive = false;
  let primaryStartedAt: number | null = null;
  const activeTools = new Map<string, number | null>();
  const providerManaged = new Set<string>();
  let providerActive = new Set<string>();
  for (const message of messages) {
    if (isPrimary(message)) {
      if (message.type === "turn-started") {
        primaryActive = true;
        primaryStartedAt = message.startedAtMs ?? null;
      } else if (message.type === "turn-completed") {
        primaryActive = false;
        primaryStartedAt = null;
      }
    }
    if (message.type === "background-state") {
      providerActive = new Set(message.itemIds ?? []);
      for (const id of providerActive) providerManaged.add(id);
    }
    if (message.itemType === "tool" && message.itemId !== null && message.itemId !== undefined) {
      if (message.type === "item-started") {
        activeTools.set(
          message.itemId,
          message.startedAtMs ?? activeTools.get(message.itemId) ?? null,
        );
      } else if (message.type === "item-completed" || message.type === "item-retracted") {
        activeTools.delete(message.itemId);
      }
    }
  }
  for (const id of providerManaged) {
    if (!providerActive.has(id)) activeTools.delete(id);
  }
  return { activeTools, primaryActive, primaryStartedAt, providerActive };
}

export type PendingRequestKind = RequestKind;

export interface PendingRequest {
  key: string;
  kind: PendingRequestKind;
  requestId: string;
}

/**
 * The latest request still open in the shared lifecycle fold — the same resolution-based signal that keeps
 * the card's buttons on screen. A turn boundary does not clear it: a request is answerable for exactly as
 * long as it is unresolved, so the hotkey chip and chord never drop off a card that still shows its buttons.
 */
export function pendingRequest(messages: readonly AgentPaneUpdate[]): PendingRequest | null {
  let latest: PendingRequest | null = null;
  for (const lifecycle of requestLifecycles(messages)) {
    if (lifecycle.resolvedStatus === null) {
      latest = {
        key: lifecycle.key,
        kind: lifecycle.kind,
        requestId: lifecycle.requestId,
      };
    }
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
