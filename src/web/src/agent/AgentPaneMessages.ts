import type { AgentPaneUpdate } from "../bridge";
import type {
  AgentActivityStep,
  AgentTranscriptEntry,
  AgentTranscriptTone,
} from "./AgentPaneTranscriptTypes";

interface MutableActivity extends AgentTranscriptEntry {
  latestStepId: string | null;
  stepIndexes: Map<string, number>;
}

interface ActivityStepUpdate {
  promote: boolean;
  step: AgentActivityStep;
}

export function toAgentTranscript(messages: readonly AgentPaneUpdate[]): AgentTranscriptEntry[] {
  const resolved = collectResolved(messages);
  const entries: (AgentTranscriptEntry | MutableActivity)[] = [];
  const activities = new Map<string, MutableActivity>();
  let activeTurn = "startup";
  let sequence = 0;

  for (const message of messages) {
    const durable = durableEntry(message, resolved, sequence);
    if (durable !== null) {
      entries.push(durable);
      if (message.type === "user-message") {
        activeTurn = message.turnId ?? `turn-${sequence}`;
      }
      sequence += 1;
      continue;
    }

    const update = activityStep(message);
    if (update === null) {
      continue;
    }

    const turnKey = message.turnId ?? activeTurn;
    const activity = activityFor(turnKey, entries, activities);
    upsertStep(activity, update);
  }

  return entries.map((entry) => stripMutable(entry));
}

function collectResolved(messages: readonly AgentPaneUpdate[]): ReadonlyMap<string, string> {
  const resolved = new Map<string, string>();
  for (const message of messages) {
    if (!isResolutionMessage(message) || !hasItemId(message)) {
      continue;
    }

    const status = normalizeStatus(message.status) ?? "resolved";
    const current = resolved.get(message.itemId);
    if (current === undefined || current === "resolved") {
      resolved.set(message.itemId, status);
    }
  }
  return resolved;
}

function durableEntry(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, string>,
  sequence: number,
): AgentTranscriptEntry | null {
  const status = displayStatus(message, resolved);
  switch (message.type) {
    case "approval-requested":
      return entry(message, sequence, "request", "pending", "Permission", status);
    case "edit-location":
      return entry(message, sequence, "notice", "system", "Edit", status);
    case "error":
      return entry(message, sequence, "notice", "error", "Error", status);
    case "input-requested":
      return entry(message, sequence, "request", "pending", "Input", status);
    case "interrupted":
      return entry(message, sequence, "notice", "warning", "Interrupted", status);
    case "item-completed":
      return message.itemType === "agentMessage"
        ? entry(message, sequence, "message", "assistant", "Codex", null)
        : null;
    case "user-image":
      return entry(message, sequence, "message", "user", "Image", status);
    case "user-message":
      return entry(message, sequence, "message", "user", "You", null);
    case "user-steer":
      return entry(message, sequence, "message", "user", "Steer", null);
    case "warning":
      return entry(message, sequence, "notice", "warning", "Warning", status);
    default:
      return null;
  }
}

function entry(
  message: AgentPaneUpdate,
  sequence: number,
  kind: AgentTranscriptEntry["kind"],
  tone: AgentTranscriptTone,
  label: string,
  status: string | null,
): AgentTranscriptEntry {
  return {
    actionMessage: actionMessage(message),
    details: [],
    id: message.itemId ?? `${message.type}-${sequence}`,
    kind,
    label,
    status,
    summary: normalizeText(message.summary),
    text: normalizeText(message.text),
    tone,
  };
}

function actionMessage(message: AgentPaneUpdate): AgentPaneUpdate | null {
  return message.type === "approval-requested" ||
    message.type === "edit-location" ||
    message.type === "input-requested"
    ? message
    : null;
}

function activityStep(message: AgentPaneUpdate): ActivityStepUpdate | null {
  switch (message.type) {
    case "file-patch-updated":
      return promotedStep(message, "patch", message.summary, "updated", "muted");
    case "item-completed":
      return message.itemType === "agentMessage"
        ? null
        : promotedStep(
            message,
            activityPrefix(message),
            message.summary,
            normalizeStatus(message.status),
            stepTone(message),
          );
    case "item-started":
      return promotedStep(message, activityPrefix(message), message.summary, "running", "running");
    case "turn-diff":
      return detailStep(message, "diff", "ready", "ready", "muted");
    default:
      return null;
  }
}

function promotedStep(
  message: AgentPaneUpdate,
  prefix: string,
  summary: string | null | undefined,
  status: string | null,
  tone: AgentActivityStep["tone"],
): ActivityStepUpdate {
  return { promote: true, step: step(message, prefix, summary, status, tone) };
}

function detailStep(
  message: AgentPaneUpdate,
  prefix: string,
  summary: string | null | undefined,
  status: string | null,
  tone: AgentActivityStep["tone"],
): ActivityStepUpdate {
  return { promote: false, step: step(message, prefix, summary, status, tone) };
}

function step(
  message: AgentPaneUpdate,
  prefix: string,
  summary: string | null | undefined,
  status: string | null,
  tone: AgentActivityStep["tone"],
): AgentActivityStep {
  const normalized = normalizeText(summary);
  return {
    detailText: normalizeText(message.text),
    id: message.itemId ?? `${message.type}:${message.turnId ?? "session"}:${prefix}`,
    label: normalized === null ? prefix : `${prefix} ${normalized}`,
    status,
    tone,
  };
}

function activityFor(
  turnKey: string,
  entries: (AgentTranscriptEntry | MutableActivity)[],
  activities: Map<string, MutableActivity>,
): MutableActivity {
  const existing = activities.get(turnKey);
  if (existing !== undefined) {
    return existing;
  }

  const activity: MutableActivity = {
    actionMessage: null,
    details: [],
    id: `activity-${turnKey}`,
    kind: "activity",
    label: "Working",
    latestStepId: null,
    status: null,
    stepIndexes: new Map<string, number>(),
    summary: null,
    text: null,
    tone: "activity",
  };
  activities.set(turnKey, activity);
  entries.push(activity);
  return activity;
}

function upsertStep(activity: MutableActivity, update: ActivityStepUpdate): void {
  const step = update.step;
  const index = activity.stepIndexes.get(step.id);
  if (index === undefined) {
    activity.stepIndexes.set(step.id, activity.details.length);
    activity.details.push(step);
  } else {
    activity.details[index] = step;
  }

  if (update.promote || activity.latestStepId === null) {
    activity.latestStepId = step.id;
    activity.summary = step.label;
    activity.status = step.status;
    activity.tone = step.tone === "failed" ? "error" : "activity";
  }
}

function stripMutable(entry: AgentTranscriptEntry | MutableActivity): AgentTranscriptEntry {
  return {
    actionMessage: entry.actionMessage,
    details: entry.details,
    id: entry.id,
    kind: entry.kind,
    label: entry.label,
    status: entry.status,
    summary: entry.summary,
    text: entry.text,
    tone: entry.tone,
  };
}

function activityPrefix(message: AgentPaneUpdate): string {
  switch (message.itemType) {
    case "commandExecution":
      return "cmd";
    case "dynamicToolCall":
    case "mcpToolCall":
      return "tool";
    case "fileChange":
      return "edit";
    case "webSearch":
      return "search";
    default:
      return humanize(message.itemType ?? message.type).toLowerCase();
  }
}

function stepTone(message: AgentPaneUpdate): AgentActivityStep["tone"] {
  const status = normalizeStatus(message.status);
  if (status === "failed" || status === "error") {
    return "failed";
  }
  if (status === "pending") {
    return "pending";
  }
  if (status === "running") {
    return "running";
  }
  return "muted";
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

function normalizeText(text: string | null | undefined): string | null {
  if (text === null || text === undefined || text.length === 0) {
    return null;
  }

  return text;
}

function hasItemId(message: AgentPaneUpdate): message is AgentPaneUpdate & { itemId: string } {
  return message.itemId !== null && message.itemId !== undefined && message.itemId.length > 0;
}

function isResolutionMessage(message: AgentPaneUpdate): boolean {
  return message.type === "approval-resolved" || message.type === "input-resolved";
}

function humanize(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/[-_/]+/g, " ")
    .replace(/^./, (first) => first.toUpperCase());
}
