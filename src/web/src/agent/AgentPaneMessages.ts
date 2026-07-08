import type { AgentPaneUpdate } from "../bridge";

export interface VisibleAgentMessage extends AgentPaneUpdate {
  displayStatus: string | null;
  displaySummary: string | null;
  displayText: string | null;
  displayType: string;
}

export function toVisibleAgentMessages(
  messages: readonly AgentPaneUpdate[],
): VisibleAgentMessage[] {
  const completedItems = new Set<string>();
  const resolved = new Map<string, string>();
  for (const message of messages) {
    if (hasCompletedItem(message)) {
      completedItems.add(message.itemId);
    }

    if (!isResolutionMessage(message) || !hasItemId(message)) {
      continue;
    }

    const status = normalizeStatus(message.status) ?? "resolved";
    const current = resolved.get(message.itemId);
    if (current === undefined || current === "resolved") {
      resolved.set(message.itemId, status);
    }
  }

  return messages.flatMap((message): VisibleAgentMessage[] => {
    if (isHiddenMessage(message, completedItems)) {
      return [];
    }

    return [
      {
        ...message,
        displayStatus: displayStatus(message, resolved),
        displaySummary: normalizeText(message.summary),
        displayText: normalizeText(message.text),
        displayType: displayType(message),
      },
    ];
  });
}

function isHiddenMessage(message: AgentPaneUpdate, completedItems: ReadonlySet<string>): boolean {
  return (
    message.type === "draft" ||
    message.type === "status" ||
    message.type === "turn-started" ||
    message.type === "turn-completed" ||
    message.type === "turn-interrupted" ||
    (message.type === "item-started" && hasItemId(message) && completedItems.has(message.itemId)) ||
    isResolutionMessage(message)
  );
}

function hasCompletedItem(
  message: AgentPaneUpdate,
): message is AgentPaneUpdate & { itemId: string } {
  return message.type === "item-completed" && hasItemId(message);
}

function hasItemId(message: AgentPaneUpdate): message is AgentPaneUpdate & { itemId: string } {
  return message.itemId !== null && message.itemId !== undefined && message.itemId.length > 0;
}

function isResolutionMessage(message: AgentPaneUpdate): boolean {
  return message.type === "approval-resolved" || message.type === "input-resolved";
}

function displayStatus(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, string>,
): string | null {
  if (hasItemId(message)) {
    return resolved.get(message.itemId) ?? normalizeStatus(message.status);
  }

  return normalizeStatus(message.status);
}

function normalizeStatus(status: string | null | undefined): string | null {
  switch (status) {
    case "":
    case null:
    case undefined:
      return null;
    case "accept":
      return "accepted";
    case "reject":
      return "denied";
    case "inProgress":
      return "running";
    default:
      return status;
  }
}

function normalizeText(text: string | null | undefined): string | null {
  if (text === null || text === undefined || text.length === 0) {
    return null;
  }

  return text;
}

function displayType(message: AgentPaneUpdate): string {
  const type = message.itemType ?? message.type;
  switch (type) {
    case "agentMessage":
      return "Codex";
    case "approval-requested":
      return "Permission request";
    case "commandExecution":
      return "Command";
    case "dynamicToolCall":
    case "mcpToolCall":
      return "Tool";
    case "fileChange":
      return "File change";
    case "file-patch-updated":
      return "File patch";
    case "input-requested":
      return "Input needed";
    case "user-image":
      return "Image";
    case "user-message":
      return "You";
    case "user-steer":
      return "Steer";
    case "webSearch":
      return "Web search";
    default:
      return humanize(type);
  }
}

function humanize(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[-_/]+/g, " ")
    .replace(/^./, (first) => first.toUpperCase());
}
