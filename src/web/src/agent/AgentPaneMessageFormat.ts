import type { AgentPaneUpdate } from "../bridge";
import { paneItemIdentity } from "./AgentPaneIdentity";

export function displayStatus(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, string>,
): string | null {
  if (message.type === "edit-location") {
    return null;
  }

  if (hasItemId(message)) {
    const key = paneItemIdentity(message);
    return (key === null ? undefined : resolved.get(key)) ?? normalizeStatus(message.status);
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
  return message.type === "approval-resolved" || message.type === "input-resolved";
}

export type RequestKind = "approval" | "input";

export interface RequestLifecycle {
  /** Thread+turn+item-scoped pane identity — the key a resolution matches its request on. */
  readonly key: string;
  /** The request's own itemId, used to answer it. */
  readonly requestId: string;
  readonly kind: RequestKind;
  /** null while the request is open; the normalized status once its matching resolution arrives. */
  readonly resolvedStatus: string | null;
}

// A Map, not an object literal, so a message type like "toString" can't match through the prototype chain.
const REQUEST_KIND = new Map<string, RequestKind>([
  ["approval-requested", "approval"],
  ["input-requested", "input"],
]);

/**
 * Every user request folded to its lifecycle, in first-requested order — the one source of truth for "is
 * this request still open, and how did it resolve." The card's resolved-status lookup and the keyboard's
 * newest-open approval both derive from this, so the two can never drift apart.
 */
export function requestLifecycles(messages: readonly AgentPaneUpdate[]): RequestLifecycle[] {
  const byKey = new Map<
    string,
    { requestId: string; kind: RequestKind; resolvedStatus: string | null }
  >();
  for (const message of messages) {
    if (!hasItemId(message)) {
      continue;
    }
    const key = paneItemIdentity(message);
    if (key === null) {
      continue;
    }
    const kind = REQUEST_KIND.get(message.type);
    if (kind !== undefined) {
      if (!byKey.has(key)) {
        byKey.set(key, { requestId: message.itemId, kind, resolvedStatus: null });
      }
    } else if (isResolutionMessage(message)) {
      const record = byKey.get(key);
      // A decision status ("accepted", "denied", …) wins over a bare "resolved" mirror; a resolution with
      // no matching request is inert — no card carries its key, so nothing looks it up.
      if (
        record !== undefined &&
        (record.resolvedStatus === null || record.resolvedStatus === "resolved")
      ) {
        record.resolvedStatus = normalizeStatus(message.status) ?? "resolved";
      }
    }
  }
  return [...byKey.entries()].map(([key, record]) => ({ key, ...record }));
}
