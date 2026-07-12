import type { AgentPaneUpdate } from "../bridge";
import { summarizeActivity } from "./AgentPaneActivitySummary";
import {
  displayStatus,
  hasItemId,
  isResolutionMessage,
  normalizeStatus,
  normalizeText,
} from "./AgentPaneMessageFormat";
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
  const updates = coalesceStreaming(messages);
  const resolved = collectResolved(messages);
  const reportedTurnErrors = new Set(
    messages.flatMap((message) =>
      message.type === "error" && message.turnId != null ? [message.turnId] : [],
    ),
  );
  const entries: (AgentTranscriptEntry | MutableActivity)[] = [];
  const activities = new Map<string, MutableActivity>();
  let activeTurn = "startup";
  let sequence = 0;

  for (const message of updates) {
    const durable = durableEntry(message, resolved, reportedTurnErrors, sequence);
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

  return collapseAssistantUpdates(
    collapseEditLocations(entries.map((entry) => stripMutable(entry))),
  );
}

function coalesceStreaming(messages: readonly AgentPaneUpdate[]): AgentPaneUpdate[] {
  const output: AgentPaneUpdate[] = [];
  const indexes = new Map<string, number>();
  for (const message of messages) {
    const key = message.itemId == null ? null : `${message.turnId ?? "session"}:${message.itemId}`;
    if (message.type === "item-started" && key !== null) {
      indexes.set(key, output.length);
      output.push(message);
      continue;
    }
    if (isDelta(message) && key !== null) {
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
    if (message.type === "item-completed" && key !== null && indexes.has(key)) {
      output[indexes.get(key)!] = message;
      indexes.delete(key);
      continue;
    }
    output.push(message);
  }
  return output;
}

function isDelta(message: AgentPaneUpdate): boolean {
  return (
    message.type === "agent-message-delta" ||
    message.type === "plan-delta" ||
    message.type === "command-output-delta"
  );
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
  reportedTurnErrors: ReadonlySet<string>,
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
    case "turn-completed":
      return normalizeStatus(message.status) === "failed" &&
        (message.turnId == null || !reportedTurnErrors.has(message.turnId)) &&
        (normalizeText(message.summary) !== null || normalizeText(message.text) !== null)
        ? entry(message, sequence, "notice", "error", "Error", "failed")
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
      return promotedStep(message, "edit", message.summary, "updated", "muted");
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
  category: string,
  summary: string | null | undefined,
  status: string | null,
  tone: AgentActivityStep["tone"],
): ActivityStepUpdate {
  return { promote: true, step: step(message, category, summary, status, tone) };
}

function detailStep(
  message: AgentPaneUpdate,
  category: string,
  summary: string | null | undefined,
  status: string | null,
  tone: AgentActivityStep["tone"],
): ActivityStepUpdate {
  return { promote: false, step: step(message, category, summary, status, tone) };
}

function step(
  message: AgentPaneUpdate,
  category: string,
  summary: string | null | undefined,
  status: string | null,
  tone: AgentActivityStep["tone"],
): AgentActivityStep {
  const normalized = normalizeText(summary);
  return {
    category,
    detailText: normalizeText(message.text),
    id: message.itemId ?? `${message.type}:${message.turnId ?? "session"}:${category}`,
    label: normalized === null ? category : `${category} ${normalized}`,
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

  if (update.promote) {
    activity.latestStepId = step.id;
  }

  const state = summarizeActivity(activity.details);
  activity.summary = state.summary;
  activity.status = state.status;
  activity.tone = state.tone;
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

function collapseAssistantUpdates(entries: AgentTranscriptEntry[]): AgentTranscriptEntry[] {
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
    details: edits.map((entry) => editStep(entry)),
    id: `edits-${edits[0]?.id ?? "empty"}`,
    kind: "activity",
    label: "Edits",
    status: null,
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
  output.push(...clusterActivity(collapseEarlierAssistant(group)));
}

function collapseEarlierAssistant(group: AgentTranscriptEntry[]): AgentTranscriptEntry[] {
  const assistantIndexes = group.flatMap((entry, index) =>
    isAssistantMessage(entry) ? [index] : [],
  );
  if (assistantIndexes.length <= 1) {
    return group;
  }

  const lastAssistantIndex = assistantIndexes[assistantIndexes.length - 1];
  const collapsed = assistantIndexes
    .slice(0, -1)
    .map((index) => group[index])
    .filter((entry) => entry !== undefined);
  const collapsedIndexes = new Set(assistantIndexes.slice(0, -1));
  const output: AgentTranscriptEntry[] = [];
  for (let i = 0; i < group.length; i += 1) {
    const entry = group[i];
    if (entry === undefined || collapsedIndexes.has(i)) {
      continue;
    }

    if (i === lastAssistantIndex) {
      output.push(earlierUpdatesEntry(collapsed));
    }

    output.push(entry);
  }
  return output;
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
    if (entry !== undefined && (isAssistantMessage(entry) || isPendingRequest(entry))) {
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

function earlierUpdatesEntry(entries: AgentTranscriptEntry[]): AgentTranscriptEntry {
  return {
    actionMessage: null,
    details: entries.map((entry, index) => ({
      category: "update",
      detailText: entry.text ?? entry.summary,
      id: `${entry.id}:update`,
      label: updateLabel(entry, index),
      status: null,
      tone: "muted",
    })),
    id: `updates-${entries[0]?.id ?? "empty"}`,
    kind: "activity",
    label: "Earlier updates",
    status: null,
    summary: null,
    text: null,
    tone: "activity",
  };
}

function updateLabel(entry: AgentTranscriptEntry, index: number): string {
  const firstLine = (entry.text ?? entry.summary)?.split(/\r?\n/, 1)[0]?.trim();
  return firstLine === undefined || firstLine.length === 0 ? `update ${index + 1}` : firstLine;
}

function isAssistantMessage(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "message" && entry.tone === "assistant";
}

function isUserMessage(entry: AgentTranscriptEntry): boolean {
  return entry.kind === "message" && entry.tone === "user";
}

function isEditLocation(entry: AgentTranscriptEntry): boolean {
  return entry.actionMessage?.type === "edit-location";
}

function activityPrefix(message: AgentPaneUpdate): string {
  if (message.category !== null && message.category !== undefined) {
    return message.category;
  }

  switch (message.itemType) {
    case "commandExecution":
      return "command";
    case "dynamicToolCall":
    case "mcpToolCall":
      return "tool";
    case "fileChange":
      return "edit";
    case "webSearch":
      return "search";
    default:
      return "step";
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
