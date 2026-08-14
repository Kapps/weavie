import type { AgentPaneUpdate } from "../bridge";
import { isAgentActivity, ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import { isAgentPaneDelta } from "./AgentPaneDelta";
import { paneActivityIdentity, paneItemIdentity, paneTurnIdentity } from "./AgentPaneIdentity";
import {
  displayStatus,
  normalizeStatus,
  normalizeText,
  requestLifecycles,
} from "./AgentPaneMessageFormat";
import type {
  AgentActivityStep,
  AgentTranscriptEntry,
  AgentTranscriptTone,
} from "./AgentPaneTranscriptTypes";
import { planIdentity } from "./agent-plan";

interface MutableActivity extends AgentTranscriptEntry {
  projection: ProjectedAgentActivity;
}

export interface AgentTranscriptProjection {
  activities: ReadonlyMap<string, ProjectedAgentActivity>;
  entries: AgentTranscriptEntry[];
}

export function toAgentTranscript(messages: readonly AgentPaneUpdate[]): AgentTranscriptEntry[] {
  return projectAgentTranscript(messages).entries;
}

export function projectAgentTranscript(
  messages: readonly AgentPaneUpdate[],
): AgentTranscriptProjection {
  const updates = coalesceStreaming(messages);
  const resolved = collectResolved(messages);
  const reportedTurnErrors = new Set<string>();
  for (const message of messages) {
    if (message.type === "error") {
      const key = paneTurnIdentity(message);
      if (key !== null) {
        reportedTurnErrors.add(key);
      }
    }
  }
  const entries: (AgentTranscriptEntry | MutableActivity)[] = [];
  const activities = new Map<string, MutableActivity>();
  const knownTurns = new Set<string>();
  let activeTurn = "startup";
  let previousWasUserInput = false;
  let sequence = 0;
  let previousThreadId: string | null | undefined;
  let previousTurnId: string | null | undefined;
  let previousTurnKey: string | null = null;
  let hasPreviousTurn = false;
  let previousActivityThreadId: string | null | undefined;
  let previousActivityTurnId: string | null | undefined;
  let previousActivityFallback = "";
  let previousActivityKey = "";
  let hasPreviousActivity = false;

  for (const message of updates) {
    const turnKey: string | null =
      hasPreviousTurn && message.threadId === previousThreadId && message.turnId === previousTurnId
        ? previousTurnKey
        : paneTurnIdentity(message);
    previousThreadId = message.threadId;
    previousTurnId = message.turnId;
    previousTurnKey = turnKey;
    hasPreviousTurn = true;
    const startsUnknownTurn = turnKey === null || !knownTurns.has(turnKey);
    const startsTurn =
      (message.type === "user-message" && startsUnknownTurn) ||
      (message.type === "user-image" && !previousWasUserInput && startsUnknownTurn);
    previousWasUserInput = isUserInput(message);
    if (turnKey !== null) {
      knownTurns.add(turnKey);
    }

    const durable = durableEntry(message, resolved, reportedTurnErrors, sequence);
    if (durable !== null) {
      if (startsTurn) {
        durable.turnStart = true;
      }
      entries.push(durable);
      if (message.type === "user-message") {
        activeTurn = message.turnId ?? `turn-${sequence}`;
      }
      sequence += 1;
      continue;
    }

    if (!isAgentActivity(message)) {
      continue;
    }

    const activityKey =
      hasPreviousActivity &&
      message.threadId === previousActivityThreadId &&
      message.turnId === previousActivityTurnId &&
      activeTurn === previousActivityFallback
        ? previousActivityKey
        : paneActivityIdentity(message, activeTurn);
    previousActivityThreadId = message.threadId;
    previousActivityTurnId = message.turnId;
    previousActivityFallback = activeTurn;
    previousActivityKey = activityKey;
    hasPreviousActivity = true;
    const activity = activityFor(activityKey, entries, activities);
    activity.projection.upsert(message);
  }

  for (const activity of activities.values()) {
    const state = activity.projection.summary();
    activity.detailCount = activity.projection.count;
    activity.summary = state.summary;
    activity.status = state.status;
    activity.tone = state.tone;
  }

  return {
    activities: new Map(
      Array.from(activities.values(), (activity) => [activity.id, activity.projection]),
    ),
    entries: clusterTurnActivity(
      collapseEditLocations(entries.map((entry) => stripMutable(entry))),
    ),
  };
}

function coalesceStreaming(messages: readonly AgentPaneUpdate[]): AgentPaneUpdate[] {
  const output: AgentPaneUpdate[] = [];
  const indexes = new Map<string, number>();
  for (const message of messages) {
    const key = paneItemIdentity(message);
    if (message.type === "item-started" && key !== null) {
      indexes.set(key, output.length);
      output.push(message);
      continue;
    }
    if (isAgentPaneDelta(message) && key !== null) {
      let index = indexes.get(key);
      if (index === undefined) {
        index = output.length;
        indexes.set(key, index);
        output.push({
          ...message,
          type: message.type === "agent-message-delta" ? "item-completed" : "item-started",
          summary: message.itemType === "plan" ? "plan" : null,
          text: "",
        });
      }
      const current = output[index]!;
      output[index] = {
        ...current,
        text: `${current.text ?? ""}${message.text ?? ""}`,
        status: "inProgress",
      };
      continue;
    }
    if (
      (message.type === "item-completed" || message.type === "item-retracted") &&
      key !== null &&
      indexes.has(key)
    ) {
      output[indexes.get(key)!] = message;
      continue;
    }
    if ((message.type === "item-completed" || message.type === "item-retracted") && key !== null) {
      indexes.set(key, output.length);
    }
    output.push(message);
  }
  return output;
}

function collectResolved(messages: readonly AgentPaneUpdate[]): ReadonlyMap<string, string> {
  const resolved = new Map<string, string>();
  for (const lifecycle of requestLifecycles(messages)) {
    if (lifecycle.resolvedStatus !== null) {
      resolved.set(lifecycle.key, lifecycle.resolvedStatus);
    }
  }
  return resolved;
}

function durableEntry(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, string>,
  reportedTurnErrors: ReadonlySet<string>,
  sequence: number,
): AgentTranscriptEntry | null {
  const status = displayStatus(message, resolved);
  switch (message.type) {
    case "approval-requested":
      return entry(message, sequence, "request", "pending", "Permission", status);
    case "authentication-requested":
      return entry(message, sequence, "request", "pending", "Sign in", status);
    case "edit-location":
      return entry(message, sequence, "notice", "system", "Edit", status);
    case "error":
      return entry(message, sequence, "notice", "error", "Error", status);
    case "goal":
      return entry(message, sequence, "notice", "system", "Goal", status);
    case "input-requested":
      return entry(message, sequence, "request", "pending", "Input", status);
    case "interrupted":
      return entry(message, sequence, "notice", "warning", "Interrupted", status);
    case "item-completed":
      if (message.itemType === "agentMessage") {
        return entry(message, sequence, "message", "assistant", "Agent", null);
      }
      return message.itemType === "plan" ? planEntry(message, sequence) : null;
    case "turn-completed": {
      const turnKey = paneTurnIdentity(message);
      return normalizeStatus(message.status) === "failed" &&
        (turnKey === null || !reportedTurnErrors.has(turnKey)) &&
        (normalizeText(message.summary) !== null || normalizeText(message.text) !== null)
        ? entry(message, sequence, "notice", "error", "Error", "failed")
        : null;
    }
    case "user-image":
      return entry(message, sequence, "message", "user", "Image", status);
    case "user-message":
      return entry(message, sequence, "message", "user", "You", null);
    case "user-steer":
      return entry(message, sequence, "message", "user", "Steer", null);
    case "warning":
      return entry(message, sequence, "notice", "warning", "Warning", status);
    case "notice":
      return entry(message, sequence, "notice", "system", "Notice", status);
    default:
      return null;
  }
}

function planEntry(message: AgentPaneUpdate, sequence: number): AgentTranscriptEntry {
  const identity = planIdentity(message);
  return {
    actionMessage: identity === null ? null : message,
    detailCount: 0,
    details: [],
    id: paneItemIdentity(message) ?? `plan-${sequence}`,
    kind: "plan",
    label: "Plan",
    status: null,
    streaming: false,
    summary: identity === null ? "Plan is unavailable" : "Ready to review in the editor",
    text: null,
    tone: "assistant",
  };
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
    detailCount: 0,
    details: [],
    id: paneItemIdentity(message) ?? `${message.type}-${sequence}`,
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

function activityFor(
  turnKey: string,
  entries: (AgentTranscriptEntry | MutableActivity)[],
  activities: Map<string, MutableActivity>,
): MutableActivity {
  const existing = activities.get(turnKey);
  if (existing !== undefined) {
    return existing;
  }

  const projection = new ProjectedAgentActivity();
  const activity: MutableActivity = {
    projection,
    actionMessage: null,
    detailCount: 0,
    details: [],
    id: `activity-${turnKey}`,
    kind: "activity",
    label: "Working",
    status: null,
    streaming: false,
    summary: null,
    text: null,
    tone: "activity",
  };
  activities.set(turnKey, activity);
  entries.push(activity);
  return activity;
}

function stripMutable(entry: AgentTranscriptEntry | MutableActivity): AgentTranscriptEntry {
  return {
    ...(entry.turnStart === true ? { turnStart: true as const } : {}),
    actionMessage: entry.actionMessage,
    detailCount: entry.detailCount,
    details: entry.details,
    id: entry.id,
    kind: entry.kind,
    label: entry.label,
    status: entry.status,
    streaming: entry.streaming,
    summary: entry.summary,
    text: entry.text,
    tone: entry.tone,
  };
}

function clusterTurnActivity(entries: AgentTranscriptEntry[]): AgentTranscriptEntry[] {
  const output: AgentTranscriptEntry[] = [];
  let group: AgentTranscriptEntry[] = [];
  for (const entry of entries) {
    if (isUserMessage(entry)) {
      flushGroup(output, group);
      group = [];
      output.push(entry);
    } else {
      group.push(entry);
    }
  }

  flushGroup(output, group);
  return output;
}

function collapseEditLocations(entries: AgentTranscriptEntry[]): AgentTranscriptEntry[] {
  const output: AgentTranscriptEntry[] = [];
  let edits: AgentTranscriptEntry[] = [];
  for (const entry of entries) {
    if (isEditLocation(entry)) {
      edits.push(entry);
      continue;
    }

    flushEdits(output, edits);
    edits = [];
    output.push(entry);
  }

  flushEdits(output, edits);
  return output;
}

function flushEdits(output: AgentTranscriptEntry[], edits: AgentTranscriptEntry[]): void {
  if (edits.length === 0) {
    return;
  }

  if (edits.length === 1) {
    output.push(edits[0]!);
    return;
  }

  output.push({
    actionMessage: null,
    detailCount: edits.length,
    details: edits.map((entry) => editStep(entry)),
    id: `edits-${edits[0]?.id ?? "empty"}`,
    kind: "activity",
    label: "Edits",
    status: null,
    streaming: false,
    summary: `edited ${edits.length} files`,
    text: null,
    tone: "activity",
  });
}

function editStep(entry: AgentTranscriptEntry): AgentActivityStep {
  const step: AgentActivityStep = {
    category: "edit",
    detailText: null,
    id: `${entry.id}:edit`,
    label: entry.text ?? entry.summary ?? "edit",
    status: null,
    tone: "muted",
  };
  return entry.actionMessage === null ? step : { ...step, actionMessage: entry.actionMessage };
}

function flushGroup(output: AgentTranscriptEntry[], group: AgentTranscriptEntry[]): void {
  output.push(...clusterActivity(group));
}

// Keep a turn's activity hugging the bottom — just above the result, or the pending request while
// blocked, or the segment end while streaming — so live work stays in view instead of scrolling away.
function clusterActivity(group: AgentTranscriptEntry[]): AgentTranscriptEntry[] {
  const anchor = lastAnchorIndex(group);
  const pivot = anchor < 0 ? group.length : anchor;
  const head = group.slice(0, pivot);
  return [
    ...head.filter((entry) => !isActivityEntry(entry)),
    ...head.filter((entry) => isActivityEntry(entry)),
    ...group.slice(pivot),
  ];
}

function lastAnchorIndex(entries: AgentTranscriptEntry[]): number {
  for (let i = entries.length - 1; i >= 0; i -= 1) {
    const entry = entries[i];
    if (entry !== undefined && (isAssistantResult(entry) || isPendingRequest(entry))) {
      return i;
    }
  }
  return -1;
}

function isActivityEntry(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "activity";
}

function isPendingRequest(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "request" && entry.status === "pending";
}

function isAssistantResult(entry: AgentTranscriptEntry): boolean {
  return entry.tone === "assistant" && (entry.kind === "message" || entry.kind === "plan");
}

function isUserMessage(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "message" && entry.tone === "user";
}

function isUserInput(message: AgentPaneUpdate): boolean {
  return (
    message.type === "user-message" ||
    message.type === "user-steer" ||
    message.type === "user-image"
  );
}

function isEditLocation(entry: AgentTranscriptEntry): boolean {
  return entry.actionMessage?.type === "edit-location";
}
