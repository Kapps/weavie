import { type Accessor, batch, createSignal } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
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
  type PendingRequestKind,
  pendingRequest,
} from "./turn-progress";

export type AgentSectionLabel = "Updates" | "Results";

export interface AgentPaneModel {
  attach(): () => void;
  readonly agentTurnStartId: Accessor<string | null>;
  readonly agentTurnStartIndex: Accessor<number | null>;
  readonly entries: AgentTranscriptEntry[];
  readonly generation: Accessor<number>;
  readonly history: Accessor<readonly string[]>;
  readonly keyboardApprovalId: Accessor<string | null>;
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
  entryIndex: number;
  projection: ProjectedAgentActivity;
}

export function createAgentPaneModel(
  session: ClientSession,
  activateHistory: () => () => void,
): MutableAgentPaneModel {
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  const [generation, setGeneration] = createSignal(0);
  const [revision, setRevision] = createSignal(0);
  const [turnActive, setTurnActive] = createSignal(false);
  const [turnStartedAt, setTurnStartedAt] = createSignal<number | null>(null);
  const [pendingRequestKind, setPendingRequestKind] = createSignal<PendingRequestKind | null>(null);
  const [keyboardApprovalId, setKeyboardApprovalId] = createSignal<string | null>(null);
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
  let attached = 0;
  let deactivateHistory: (() => void) | null = null;

  const project = (updates: AgentPaneUpdate[]): void => {
    const projection = projectAgentTranscript(updates);
    for (const id of expandedActivities) {
      if (!projection.activities.has(id)) {
        expandedActivities.delete(id);
      }
    }
    for (const entry of projection.entries) {
      const activity = projection.activities.get(entry.id);
      if (activity !== undefined && expandedActivities.has(entry.id)) {
        entry.details = activity.materialize();
      }
    }

    const active = hasActiveTurn(updates);
    const request = pendingRequest(updates);
    const approvalId = request?.kind === "approval" ? request.requestId : null;
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
    for (const [entryIndex, entry] of visible.entries()) {
      const activity = projection.activities.get(entry.id);
      if (activity !== undefined) {
        activities.set(entry.id, { entryIndex, projection: activity });
      }
    }

    batch(() => {
      setEntries(reconcile(visible, { key: "id" }));
      setTurnActive(active);
      setTurnStartedAt(activeTurnStartedAt(updates));
      setPendingRequestKind(request?.kind ?? null);
      setKeyboardApprovalId(approvalId);
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
        setEntries(activity.entryIndex, "detailCount", activity.projection.count);
        if (expandedActivities.has(entries[activity.entryIndex]!.id)) {
          setEntries(
            activity.entryIndex,
            "details",
            index,
            activity.projection.materializeAt(index),
          );
        }
        changedActivities.add(activity);
      }
      for (const activity of changedActivities) {
        const state = activity.projection.summary();
        setEntries(activity.entryIndex, "summary", state.summary);
        setEntries(activity.entryIndex, "status", state.status);
        setEntries(activity.entryIndex, "tone", state.tone);
      }
      setRevision((value) => value + 1);
    });
    return true;
  };

  const model: MutableAgentPaneModel = {
    attach() {
      attached += 1;
      if (attached === 1) {
        deactivateHistory = activateHistory();
      }
      return () => {
        attached -= 1;
        if (attached === 0) {
          deactivateHistory?.();
          deactivateHistory = null;
        }
      };
    },
    agentTurnStartId,
    agentTurnStartIndex,
    entries,
    generation,
    history,
    keyboardApprovalId,
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
      if (!projectActivityChanges(changes)) {
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
      if (expanded) {
        expandedActivities.add(id);
        setEntries(activity.entryIndex, "details", activity.projection.materialize());
      } else {
        expandedActivities.delete(id);
        setEntries(activity.entryIndex, "details", []);
      }
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
