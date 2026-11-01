import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";

export function displayStatus(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, RequestResolution>,
): string | null {
  if (message.type === "edit-location") {
    return null;
  }

  if (hasItemId(message)) {
    const key = paneItemIdentity(message);
    return (
      (key === null ? undefined : resolved.get(key)?.status) ?? normalizeStatus(message.status)
    );
  }

  return normalizeStatus(message.status);
}

export function normalizeStatus(status: string | null | undefined): string | null {
  switch (status) {
    case "":
    case null:
    case undefined:
      return null;
    case "accept":
      return "accepted";
    case "acceptForSession":
      return "accepted for session";
    case "decline":
    case "reject":
      return "denied";
    case "inProgress":
      return "running";
    default:
      return status;
  }
}

export function normalizeText(text: string | null | undefined): string | null {
  return text === null || text === undefined || text.length === 0 ? null : text;
}

export function hasItemId(
  message: AgentPaneUpdate,
): message is AgentPaneUpdate & { itemId: string } {
  return message.itemId !== null && message.itemId !== undefined && message.itemId.length > 0;
}

export function isResolutionMessage(message: AgentPaneUpdate): boolean {
  return (
    message.type === "approval-resolved" ||
    message.type === "authentication-resolved" ||
    message.type === "input-resolved"
  );
}

export type RequestKind = "approval" | "authentication" | "input" | "plan-review";

/** How a request ended: its normalized status and the answers the user submitted, when any. */
export interface RequestResolution {
  readonly status: string;
  readonly answers: Record<string, string[]> | null;
}

export interface RequestLifecycle {
  /** Thread+turn+item-scoped pane identity — the key a resolution matches its request on. */
  readonly key: string;
  /** The request's own itemId, used to answer it. */
  readonly requestId: string;
  readonly kind: RequestKind;
  /** null while the request is open; how it ended once its matching resolution arrives. */
  readonly resolution: RequestResolution | null;
}

// A Map, not an object literal, so a message type like "toString" can't match through the prototype chain.
const REQUEST_KIND = new Map<string, RequestKind>([
  ["approval-requested", "approval"],
  ["authentication-requested", "authentication"],
  ["input-requested", "input"],
]);

/** How each request item ended, by pane identity — including a plan review whose item became the approved plan. */
export function requestResolutions(
  messages: readonly AgentPaneUpdate[],
): Map<string, RequestResolution> {
  const resolutions = new Map<string, RequestResolution>();
  for (const message of messages) {
    const key =
      isResolutionMessage(message) && hasItemId(message) ? paneItemIdentity(message) : null;
    // A decision status ("accepted", "denied", …) wins over a bare "resolved" mirror.
    if (key !== null && (resolutions.get(key)?.status ?? "resolved") === "resolved") {
      resolutions.set(key, {
        status: normalizeStatus(message.status) ?? "resolved",
        answers: message.answers ?? null,
      });
    }
  }
  return resolutions;
}

/**
 * Every user request folded to its lifecycle, in first-requested order — the one source of truth for "is
 * this request still open, and how did it resolve." The card's resolved-status lookup and the keyboard's
 * newest-open approval both derive from this, so the two can never drift apart.
 */
export function requestLifecycles(messages: readonly AgentPaneUpdate[]): RequestLifecycle[] {
  const byKey = new Map<string, { requestId: string; kind: RequestKind }>();
  for (const message of messages) {
    if (!hasItemId(message)) {
      continue;
    }
    const key = paneItemIdentity(message);
    // A plan review is answered from its editor tab, never by the approval chords.
    const kind =
      message.type === "approval-requested" && message.itemType === "plan-review"
        ? "plan-review"
        : REQUEST_KIND.get(message.type);
    if (key !== null && kind !== undefined && !byKey.has(key)) {
      if (message.requestId === null || message.requestId === undefined) {
        throw new Error(`${message.type} is missing its provider request id.`);
      }
      byKey.set(key, { requestId: message.requestId, kind });
    }
  }
  const resolutions = requestResolutions(messages);
  return [...byKey.entries()].map(([key, record]) => ({
    key,
    ...record,
    resolution: resolutions.get(key) ?? null,
  }));
}
