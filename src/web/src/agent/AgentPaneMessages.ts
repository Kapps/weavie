import type { AgentPaneUpdate } from "../bridge";
import { AgentActivitySegments } from "./AgentActivitySegments";
import { isAgentActivity } from "./AgentPaneActivitySummary";
import { AgentPaneBlocks } from "./AgentPaneBlocks";
import { isAgentPaneDelta } from "./AgentPaneDelta";
import { entry } from "./AgentPaneEntry";
import { paneItemIdentity, paneTurnIdentity } from "./AgentPaneIdentity";
import {
  displayStatus,
  normalizeStatus,
  normalizeText,
  type RequestResolution,
  requestResolutions,
} from "./AgentPaneMessageFormat";
import type { AgentActivityStep, AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { planIdentity } from "./agent-plan";

export interface AgentTranscriptProjection {
  currentThought: string | null;
  segments: AgentActivitySegments;
  entries: AgentTranscriptEntry[];
}

export function projectAgentTranscript(
  messages: readonly AgentPaneUpdate[],
  cards: ReadonlyMap<AgentPaneUpdate, AgentTranscriptEntry>,
): AgentTranscriptProjection {
  const resolved = requestResolutions(messages);
  const reportedTurnErrors = new Set<string>();
  for (const message of messages) {
    if (message.type === "error") {
      const key = paneTurnIdentity(message);
      if (key !== null) {
        reportedTurnErrors.add(key);
      }
    }
  }
  const entries: AgentTranscriptEntry[] = [];
  const segments = new AgentActivitySegments();
  const knownTurns = new Set<string>();
  const blocks = new AgentPaneBlocks();
  let activeTurn = "startup";
  let currentThought: string | null = null;
  let previousWasUserInput = false;
  let sequence = 0;
  let previousThreadId: string | null | undefined;
  let previousTurnId: string | null | undefined;
  let previousTurnKey: string | null = null;
  let hasPreviousTurn = false;

  for (const source of messages) {
    const message = isAgentPaneDelta(source)
      ? {
          ...source,
          status: "inProgress",
          type: source.type === "agent-message-delta" ? "item-completed" : "item-started",
        }
      : source;
    const card = cards.get(message);
    if (card !== undefined) {
      segments.boundary();
      entries.push(card);
      sequence += 1;
      continue;
    }
    if (
      message.type === "turn-started" ||
      message.type === "turn-completed" ||
      message.type === "side-conversation-closed" ||
      message.type === "side-conversation-failed" ||
      (isUserInput(message) && !message.sender && message.status !== "attached")
    )
      currentThought = null;
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
      ((message.type === "user-message" || message.type === "user-command") && startsUnknownTurn) ||
      ((message.type === "user-image" || message.type === "user-selection") &&
        !previousWasUserInput &&
        startsUnknownTurn);
    previousWasUserInput = isUserInput(message);
    if (turnKey !== null && message.type !== "turn-started") {
      knownTurns.add(turnKey);
    }

    const shown = blocks.take(message, sequence);
    if (shown !== null) {
      for (const block of shown) {
        segments.boundary();
        entries.push(block);
      }
      sequence += 1;
      continue;
    }

    const durable = durableEntry(message, resolved, reportedTurnErrors, sequence);
    if (durable !== null) {
      if (durable.kind === "thought") currentThought = durable.text;
      if (startsTurn) {
        durable.turnStart = true;
      }
      segments.boundary();
      entries.push(durable);
      if (message.type === "user-message" || message.type === "user-command") {
        activeTurn = message.turnId ?? `turn-${sequence}`;
      }
      sequence += 1;
      continue;
    }

    if (!isAgentActivity(message)) {
      continue;
    }

    const count = segments.groups.size;
    const segment = segments.add(message, activeTurn);
    if (segments.groups.size !== count) entries.push(segment.entry);
    segments.upsert(segment, message);
  }

  for (const { entry, projection } of segments.groups.values()) {
    Object.assign(entry, projection.summary(), { detailCount: projection.count });
  }

  return { currentThought, segments, entries: collapseEditLocations(entries) };
}

function durableEntry(
  message: AgentPaneUpdate,
  resolved: ReadonlyMap<string, RequestResolution>,
  reportedTurnErrors: ReadonlySet<string>,
  sequence: number,
): AgentTranscriptEntry | null {
  if (message.itemType === "thought") {
    if (normalizeText(message.text) === null) return null;
    return {
      ...entry(message, sequence, "thought", "system", "Thinking", null),
      summary: null,
    };
  }
  const status = displayStatus(message, resolved);
  const tone = status === "pending" ? "pending" : "system";
  if (message.itemType === "backgroundTask") {
    return {
      ...entry(message, sequence, "workflow", "assistant", "Workflow", message.status ?? null),
      actionMessage: message,
    };
  }
  switch (message.type) {
    case "approval-requested":
      return message.itemType === "plan-review"
        ? {
            ...entry(message, sequence, "request", tone, "Plan", status),
            summary: status === "pending" ? "Ready to review in the editor" : null,
          }
        : entry(message, sequence, "request", tone, "Permission", status);
    case "authentication-requested":
      return entry(message, sequence, "request", "pending", "Sign in", status);
    case "edit-location":
      return entry(message, sequence, "notice", "system", "Edit", status);
    case "error":
      return entry(message, sequence, "notice", "error", "Error", status);
    case "goal":
      return entry(message, sequence, "notice", "system", "Goal", status);
    case "input-requested": {
      // The card renders the request as answered, so questions and answers travel as one object.
      const key = paneItemIdentity(message);
      const answers = key === null ? null : (resolved.get(key)?.answers ?? null);
      const request = answers === null ? message : { ...message, answers };
      const label = message.itemType === "url" ? "Link" : "Question";
      return entry(request, sequence, "request", "pending", label, status);
    }
    case "interrupted":
      return entry(message, sequence, "notice", "warning", "Interrupted", status);
    case "agent-message-delta":
    case "item-completed":
      if (message.itemType === "agentMessage") {
        return entry(message, sequence, "message", "assistant", "Agent", null);
      }
      return message.itemType === "plan"
        ? planEntry(
            message,
            sequence,
            resolved.get(paneItemIdentity(message) ?? "")?.status ?? null,
          )
        : null;
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
    case "user-selection":
      // The label is the row; the selected values open beneath it.
      return {
        ...entry(message, sequence, "message", "user", "Selection", null),
        actionMessage: message,
        text: null,
      };
    case "user-message":
      return promptEntry(
        message,
        entry(message, sequence, "message", "user", promptLabel(message, "You"), null),
      );
    case "user-command":
      return promptEntry(message, entry(message, sequence, "message", "user", "Command", null));
    case "user-steer":
      return entry(message, sequence, "message", "user", promptLabel(message, "Steer"), null);
    case "warning":
      return entry(message, sequence, "notice", "warning", "Warning", status);
    case "notice":
      return entry(message, sequence, "notice", "system", "Notice", status);
    default:
      return null;
  }
}

// An approved plan review's item becomes the agent's plan, which carries the review's outcome.
function planEntry(
  message: AgentPaneUpdate,
  sequence: number,
  outcome: string | null,
): AgentTranscriptEntry {
  const identity = planIdentity(message);
  return {
    actionMessage: identity === null ? null : message,
    detailCount: 0,
    details: [],
    id: paneItemIdentity(message) ?? `plan-${sequence}`,
    kind: "plan",
    label: "Plan",
    status: outcome,
    streaming: false,
    summary:
      identity === null
        ? "Plan is unavailable"
        : outcome === null
          ? "Ready to review in the editor"
          : null,
    text: null,
    tone: "assistant",
  };
}

// A primary prompt carries its turn so the pane can rewind to just before it.
function promptEntry(message: AgentPaneUpdate, prompt: AgentTranscriptEntry): AgentTranscriptEntry {
  return message.conversationId || !message.turnId
    ? prompt
    : { ...prompt, promptTurnId: message.turnId };
}

function promptLabel(message: AgentPaneUpdate, label: string): string {
  return message.sender ? `From ${message.sender.title}` : label;
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

function isUserInput(message: AgentPaneUpdate): boolean {
  return (
    message.type === "user-message" ||
    message.type === "user-command" ||
    message.type === "user-steer" ||
    message.type === "user-image" ||
    message.type === "user-selection"
  );
}

function isEditLocation(entry: AgentTranscriptEntry): boolean {
  return entry.actionMessage?.type === "edit-location";
}
