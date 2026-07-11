import type { AgentPaneUpdate } from "../bridge";

export function displayStatus(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, string>,
): string | null {
  if (message.type === "edit-location") {
    return null;
  }

  if (hasItemId(message)) {
    return resolved.get(message.itemId) ?? normalizeStatus(message.status);
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
