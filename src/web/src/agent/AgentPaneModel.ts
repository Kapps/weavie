import { type Accessor, batch, createSignal } from "solid-js";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { clearAgentInputDrafts } from "./AgentInputDrafts";
import type { ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import { paneActivityIdentity } from "./AgentPaneIdentity";
import { projectAgentTranscript } from "./AgentPaneMessages";
import type {
  AgentActivityStep,
  AgentTranscriptEntry,
  AgentTranscriptSnapshot,
} from "./AgentPaneTranscriptTypes";
import { orderPendingRequests } from "./AgentPendingRequests";
import { computeSectionLabels, isResult, latestAgentTurnStartId } from "./AgentTranscriptLabels";
import { emptyTranscript, share, shareTranscript } from "./AgentTranscriptSnapshot";
import { type AgentPlanIdentity, latestCompletedPlan } from "./agent-plan";
import { submittedPrompts } from "./prompt-history";
import {
  activeTurnStartedAt,
  hasActiveTurn,
  hasInterruptibleActivity,
  type PendingRequestKind,
  pendingRequest,
} from "./turn-progress";

export type { AgentSectionLabel } from "./AgentPaneTranscriptTypes";

export interface AgentPaneModel {
  readonly generation: Accessor<number>;
  readonly history: Accessor<readonly string[]>;
  /** Whether the host's stored transcript has fully loaded. */
  readonly historyComplete: Accessor<boolean>;
  readonly interruptible: Accessor<boolean>;
  readonly keyboardApprovalId: Accessor<string | null>;
  readonly keyboardRequestKey: Accessor<string | null>;
  readonly latestPlan: Accessor<AgentPlanIdentity | null>;
  /** Whether the transcript is receiving live output rather than only history. */
  readonly live: Accessor<boolean>;
  readonly pendingLegacyImageCount: Accessor<number>;
  readonly pendingRequestKind: Accessor<PendingRequestKind | null>;
  readonly session: ClientSession;
  readonly transcript: Accessor<AgentTranscriptSnapshot>;
  readonly turnActive: Accessor<boolean>;
  readonly turnStartedAt: Accessor<number | null>;
  setActivityExpanded(id: string, expanded: boolean): void;
  /** The live activity step with transcript item id `itemId`, in the transcript or a nested one. */
  activityStep(itemId: string): AgentActivityStep | undefined;
}

export interface MutableAgentPaneModel extends AgentPaneModel {
  publish(updates: AgentPaneUpdate[], changes: AgentPaneUpdate[]): void;
  replace(updates: AgentPaneUpdate[]): void;
  setHistoryComplete(complete: boolean): void;
  /** Marks the transcript as receiving live output, which history loading never clears. */
  noteLive(): void;
  reset(): void;
}

interface ProjectedActivity {
  path: number[];
  projection: ProjectedAgentActivity;
}

export function createAgentPaneModel(session: ClientSession): MutableAgentPaneModel {
  const [transcript, setTranscript] = createSignal(emptyTranscript);
  const [generation, setGeneration] = createSignal(0);
  // Steps can change without changing any shown entry, so step readers track projections directly.
  const [projected, setProjected] = createSignal(undefined, { equals: false });
  const [turnActive, setTurnActive] = createSignal(false);
  const [interruptible, setInterruptible] = createSignal(false);
  const [turnStartedAt, setTurnStartedAt] = createSignal<number | null>(null);
  const [pendingRequestKind, setPendingRequestKind] = createSignal<PendingRequestKind | null>(null);
  const [keyboardApprovalId, setKeyboardApprovalId] = createSignal<string | null>(null);
  const [keyboardRequestKey, setKeyboardRequestKey] = createSignal<string | null>(null);
  const [pendingLegacyImageCount, setPendingLegacyImageCount] = createSignal(0);
  const [history, setHistory] = createSignal<readonly string[]>([]);
  const [historyComplete, setHistoryComplete] = createSignal(false);
  const [live, setLive] = createSignal(false);
  const [latestPlan, setLatestPlan] = createSignal<AgentPlanIdentity | null>(null);
  const activities = new Map<string, ProjectedActivity>();
  const expandedActivities = new Set<string>();

  const project = (updates: AgentPaneUpdate[]): void => {
    const projection = projectAgentTranscript(updates);
    for (const id of expandedActivities) {
      if (!projection.activities.has(id)) {
        expandedActivities.delete(id);
      }
    }
    visitEntries(projection.entries, (entry) => {
      const activity = projection.activities.get(entry.id);
      if (activity !== undefined && expandedActivities.has(entry.id)) {
        entry.details = activity.materialize();
      }
    });

    const active = hasActiveTurn(updates);
    const canInterrupt = hasInterruptibleActivity(updates);
    const request = pendingRequest(updates);
    const approvalId = request?.kind === "approval" ? request.requestId : null;
    const turnStartId = latestAgentTurnStartId(projection.entries);
    const ordered = orderPendingRequests(projection.entries);
    const visible = ordered.entries;
    const turnStartIndex =
      turnStartId === null ? null : visible.findIndex((entry) => entry.id === turnStartId);
    const last = visible.at(-1);
    activities.clear();
    visitEntries(visible, (entry, path) => {
      const activity = projection.activities.get(entry.id);
      if (activity !== undefined) activities.set(entry.id, { path, projection: activity });
    });

    batch(() => {
      setTranscript((previous) =>
        shareTranscript(previous, {
          agentTurnStartId: turnStartId,
          agentTurnStartIndex: turnStartIndex,
          entries: visible,
          latestResultIndex: last !== undefined && isResult(last) ? visible.length - 1 : null,
          pendingRowIndexes: ordered.pendingRowIndexes,
          sectionLabels: computeSectionLabels(visible, active),
        }),
      );
      setTurnActive(active);
      setInterruptible(canInterrupt);
      setTurnStartedAt(activeTurnStartedAt(updates));
      setPendingRequestKind(request?.kind ?? null);
      setKeyboardApprovalId(approvalId);
      setKeyboardRequestKey(request?.key ?? null);
      setPendingLegacyImageCount(countPendingLegacyImages(updates));
      setHistory(submittedPrompts(updates));
      setLatestPlan(latestCompletedPlan(updates));
      setProjected();
    });
  };

  const projectActivityChanges = (changes: AgentPaneUpdate[]): boolean => {
    const mutations: Array<{ activity: ProjectedActivity; message: AgentPaneUpdate }> = [];
    for (const message of changes) {
      if (message.conversationId) return false;
      if (message.turnId === null || message.turnId === undefined || message.turnId.length === 0) {
        return false;
      }
      const activity = activities.get(`activity-${paneActivityIdentity(message, "")}`);
      if (activity === undefined || !activity.projection.canUpsert(message)) {
        return false;
      }
      mutations.push({ activity, message });
    }
    if (mutations.length === 0) {
      return false;
    }

    const changedActivities = new Set<ProjectedActivity>();
    batch(() => {
      for (const { activity, message } of mutations) {
        const index = activity.projection.upsert(message);
        updateActivity(activity, (entry) => {
          if (!expandedActivities.has(entry.id)) {
            return { ...entry, detailCount: activity.projection.count };
          }
          const details = [...entry.details];
          details[index] = activity.projection.materializeAt(index);
          return { ...entry, detailCount: activity.projection.count, details };
        });
        changedActivities.add(activity);
      }
      for (const activity of changedActivities) {
        const state = activity.projection.summary();
        updateActivity(activity, (entry) => ({ ...entry, ...state }));
      }
      setProjected();
    });
    return true;
  };

  const updateActivity = (
    activity: ProjectedActivity,
    update: (entry: AgentTranscriptEntry) => AgentTranscriptEntry,
  ): void => {
    setTranscript((previous) => {
      const entries = replaceAtPath(previous.entries, activity.path, update);
      return entries === previous.entries ? previous : { ...previous, entries };
    });
  };

  const model: MutableAgentPaneModel = {
    generation,
    history,
    historyComplete,
    interruptible,
    keyboardApprovalId,
    keyboardRequestKey,
    latestPlan,
    live,
    pendingLegacyImageCount,
    pendingRequestKind,
    session,
    transcript,
    turnActive,
    turnStartedAt,
    publish(updates, changes) {
      const activityChanged =
        hasActiveTurn(updates) !== turnActive() ||
        hasInterruptibleActivity(updates) !== interruptible() ||
        activeTurnStartedAt(updates) !== turnStartedAt();
      if (activityChanged || !projectActivityChanges(changes)) {
        project(updates);
      }
    },
    replace(updates) {
      project(updates);
    },
    setHistoryComplete(complete) {
      batch(() => {
        setHistoryComplete(complete);
        if (!complete) setLive(false);
      });
    },
    noteLive: () => setLive(true),
    reset() {
      expandedActivities.clear();
      project([]);
      clearAgentInputDrafts(session);
      setGeneration((value) => value + 1);
    },
    activityStep(itemId) {
      projected();
      for (const activity of activities.values()) {
        const step = activity.projection.step(itemId);
        if (step !== undefined) return step;
      }
      return undefined;
    },
    setActivityExpanded(id, expanded) {
      const activity = activities.get(id);
      if (activity === undefined || expandedActivities.has(id) === expanded) {
        return;
      }
      if (expanded) expandedActivities.add(id);
      else expandedActivities.delete(id);
      updateActivity(activity, (entry) => ({
        ...entry,
        details: expanded ? activity.projection.materialize() : [],
      }));
    },
  };
  return model;
}

function countPendingLegacyImages(messages: readonly AgentPaneUpdate[]): number {
  let count = 0;
  for (const message of messages) {
    if (message.type === "user-image") {
      if (message.status === "attached") {
        count += 1;
      } else if (message.status === "submitted") {
        count = Math.max(0, count - 1);
      }
    }
  }
  return count;
}

// Copies only the entries along `path`, so every other entry keeps its identity.
function replaceAtPath(
  entries: readonly AgentTranscriptEntry[],
  path: readonly number[],
  update: (entry: AgentTranscriptEntry) => AgentTranscriptEntry,
): readonly AgentTranscriptEntry[] {
  const [index, ...rest] = path as [number, ...number[]];
  const entry = entries[index]!;
  const children = rest.length === 0 ? undefined : replaceAtPath(entry.asideEntries!, rest, update);
  const next =
    children === undefined
      ? share(entry, update(entry))
      : children === entry.asideEntries
        ? entry
        : { ...entry, asideEntries: children as AgentTranscriptEntry[] };
  if (next === entry) return entries;
  const copy = [...entries];
  copy[index] = next;
  return copy;
}

function visitEntries(
  entries: AgentTranscriptEntry[],
  visit: (entry: AgentTranscriptEntry, path: number[]) => void,
): void {
  const walk = (siblings: AgentTranscriptEntry[], parent: number[]): void => {
    siblings.forEach((entry, index) => {
      const path = [...parent, index];
      visit(entry, path);
      if (entry.asideEntries !== undefined) walk(entry.asideEntries, path);
    });
  };
  walk(entries, []);
}
