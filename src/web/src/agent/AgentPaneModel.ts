import { type Accessor, batch, createSignal } from "solid-js";
import { createStore, produce, reconcile } from "solid-js/store";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { clearAgentInputDrafts } from "./AgentInputDrafts";
import type { ProjectedAgentActivity } from "./AgentPaneActivitySummary";
import { paneActivityIdentity } from "./AgentPaneIdentity";
import { projectAgentTranscript } from "./AgentPaneMessages";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { computeSectionLabels, latestAgentTurnStartId } from "./AgentTranscriptLabels";
import { type AgentPlanIdentity, latestCompletedPlan } from "./agent-plan";
import { submittedPrompts } from "./prompt-history";
import {
  activeTurnStartedAt,
  hasActiveTurn,
  hasInterruptibleActivity,
  type PendingRequestKind,
  pendingRequest,
} from "./turn-progress";

export type AgentSectionLabel = "Updates" | "Results";

export interface AgentPaneModel {
  readonly agentTurnStartId: Accessor<string | null>;
  readonly agentTurnStartIndex: Accessor<number | null>;
  readonly entries: AgentTranscriptEntry[];
  readonly generation: Accessor<number>;
  readonly history: Accessor<readonly string[]>;
  readonly interruptible: Accessor<boolean>;
  readonly keyboardApprovalId: Accessor<string | null>;
  readonly keyboardInputId: Accessor<string | null>;
  readonly latestPlan: Accessor<AgentPlanIdentity | null>;
  readonly pendingLegacyImageCount: Accessor<number>;
  readonly pendingRequestKind: Accessor<PendingRequestKind | null>;
  readonly pinnedRequest: Accessor<AgentTranscriptEntry | null>;
  readonly revision: Accessor<number>;
  readonly sectionLabels: Accessor<ReadonlyMap<string, AgentSectionLabel>>;
  readonly session: ClientSession;
  readonly turnActive: Accessor<boolean>;
  readonly turnStartedAt: Accessor<number | null>;
  setActivityExpanded(id: string, expanded: boolean): void;
}

export interface MutableAgentPaneModel extends AgentPaneModel {
  publish(updates: AgentPaneUpdate[], changes: AgentPaneUpdate[]): void;
  replace(updates: AgentPaneUpdate[]): void;
  reset(): void;
}

interface ProjectedActivity {
  path: number[];
  projection: ProjectedAgentActivity;
}

export function createAgentPaneModel(session: ClientSession): MutableAgentPaneModel {
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  const [generation, setGeneration] = createSignal(0);
  const [revision, setRevision] = createSignal(0);
  const [turnActive, setTurnActive] = createSignal(false);
  const [interruptible, setInterruptible] = createSignal(false);
  const [turnStartedAt, setTurnStartedAt] = createSignal<number | null>(null);
  const [pendingRequestKind, setPendingRequestKind] = createSignal<PendingRequestKind | null>(null);
  const [keyboardApprovalId, setKeyboardApprovalId] = createSignal<string | null>(null);
  const [keyboardInputId, setKeyboardInputId] = createSignal<string | null>(null);
  const [pendingLegacyImageCount, setPendingLegacyImageCount] = createSignal(0);
  const [history, setHistory] = createSignal<readonly string[]>([]);
  const [latestPlan, setLatestPlan] = createSignal<AgentPlanIdentity | null>(null);
  const [pinnedRequest, setPinnedRequest] = createSignal<AgentTranscriptEntry | null>(null);
  const [agentTurnStartId, setAgentTurnStartId] = createSignal<string | null>(null);
  const [agentTurnStartIndex, setAgentTurnStartIndex] = createSignal<number | null>(null);
  const [sectionLabels, setSectionLabels] = createSignal<ReadonlyMap<string, AgentSectionLabel>>(
    new Map(),
  );
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
    const inputId = request?.kind === "input" ? request.requestId : null;
    const pinned =
      request === null
        ? null
        : (projection.entries.find((entry) => entry.id === request.key) ?? null);
    const visible =
      pinned === null ? projection.entries : projection.entries.filter((entry) => entry !== pinned);
    const turnStartId = latestAgentTurnStartId(visible);
    const turnStartIndex =
      turnStartId === null ? null : visible.findIndex((entry) => entry.id === turnStartId);
    const labels = computeSectionLabels(visible, active);
    activities.clear();
    visitEntries(visible, (entry, path) => {
      const activity = projection.activities.get(entry.id);
      if (activity !== undefined) activities.set(entry.id, { path, projection: activity });
    });

    batch(() => {
      setEntries(reconcile(visible, { key: "id" }));
      setTurnActive(active);
      setInterruptible(canInterrupt);
      setTurnStartedAt(activeTurnStartedAt(updates));
      setPendingRequestKind(request?.kind ?? null);
      setKeyboardApprovalId(approvalId);
      setKeyboardInputId(inputId);
      setPendingLegacyImageCount(countPendingLegacyImages(updates));
      setHistory(submittedPrompts(updates));
      setLatestPlan(latestCompletedPlan(updates));
      if (pinnedRequest()?.id !== pinned?.id) {
        setPinnedRequest(pinned);
      }
      setAgentTurnStartId(turnStartId);
      setAgentTurnStartIndex(turnStartIndex);
      setSectionLabels(labels);
      setRevision((value) => value + 1);
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
          entry.detailCount = activity.projection.count;
          if (expandedActivities.has(entry.id)) {
            entry.details[index] = activity.projection.materializeAt(index);
          }
        });
        changedActivities.add(activity);
      }
      for (const activity of changedActivities) {
        const state = activity.projection.summary();
        updateActivity(activity, (entry) => Object.assign(entry, state));
      }
      setRevision((value) => value + 1);
    });
    return true;
  };

  const updateActivity = (
    activity: ProjectedActivity,
    update: (entry: AgentTranscriptEntry) => void,
  ): void => {
    setEntries(
      produce((draft) => {
        let siblings = draft;
        for (const [depth, index] of activity.path.entries()) {
          const entry = siblings[index]!;
          if (depth === activity.path.length - 1) update(entry);
          else siblings = entry.asideEntries!;
        }
      }),
    );
  };

  const model: MutableAgentPaneModel = {
    agentTurnStartId,
    agentTurnStartIndex,
    entries,
    generation,
    history,
    interruptible,
    keyboardApprovalId,
    keyboardInputId,
    latestPlan,
    pendingLegacyImageCount,
    pendingRequestKind,
    pinnedRequest,
    revision,
    sectionLabels,
    session,
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
    reset() {
      expandedActivities.clear();
      project([]);
      clearAgentInputDrafts(session);
      setGeneration((value) => value + 1);
    },
    setActivityExpanded(id, expanded) {
      const activity = activities.get(id);
      if (activity === undefined || expandedActivities.has(id) === expanded) {
        return;
      }
      if (expanded) expandedActivities.add(id);
      else expandedActivities.delete(id);
      updateActivity(activity, (entry) => {
        entry.details = expanded ? activity.projection.materialize() : [];
      });
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
