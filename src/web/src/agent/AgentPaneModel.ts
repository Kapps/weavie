import { type Accessor, batch, createSignal } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { clearAgentInputDrafts } from "./AgentInputDrafts";
import { toAgentTranscript } from "./AgentPaneMessages";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { computeSectionLabels, latestAgentTurnStartId } from "./AgentTranscriptLabels";
import { hasActiveTurn, pendingRequest } from "./turn-progress";

export type AgentSectionLabel = "Updates" | "Results";

export interface AgentPaneModel {
  attach(): () => void;
  readonly agentTurnStartId: Accessor<string | null>;
  readonly agentTurnStartIndex: Accessor<number | null>;
  readonly entries: AgentTranscriptEntry[];
  readonly generation: Accessor<number>;
  readonly keyboardApprovalId: Accessor<string | null>;
  readonly messages: Accessor<AgentPaneUpdate[]>;
  readonly pinnedRequest: Accessor<AgentTranscriptEntry | null>;
  readonly revision: Accessor<number>;
  readonly sectionLabels: Accessor<ReadonlyMap<string, AgentSectionLabel>>;
  readonly session: ClientSession;
  readonly turnActive: Accessor<boolean>;
}

export interface MutableAgentPaneModel extends AgentPaneModel {
  publish(updates: AgentPaneUpdate[]): void;
  reset(): void;
}

export function createAgentPaneModel(
  session: ClientSession,
  activateHistory: () => () => void,
): MutableAgentPaneModel {
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  const [messages, setMessages] = createSignal<AgentPaneUpdate[]>([]);
  const [generation, setGeneration] = createSignal(0);
  const [revision, setRevision] = createSignal(0);
  const [turnActive, setTurnActive] = createSignal(false);
  const [keyboardApprovalId, setKeyboardApprovalId] = createSignal<string | null>(null);
  const [pinnedRequest, setPinnedRequest] = createSignal<AgentTranscriptEntry | null>(null);
  const [agentTurnStartId, setAgentTurnStartId] = createSignal<string | null>(null);
  const [agentTurnStartIndex, setAgentTurnStartIndex] = createSignal<number | null>(null);
  const [sectionLabels, setSectionLabels] = createSignal<ReadonlyMap<string, AgentSectionLabel>>(
    new Map(),
  );
  let attached = 0;
  let deactivateHistory: (() => void) | null = null;
  let projectedMessages: AgentPaneUpdate[] | null = null;

  const project = (updates: AgentPaneUpdate[]): void => {
    const projected = toAgentTranscript(updates);
    const active = hasActiveTurn(updates);
    const request = pendingRequest(updates);
    const approvalId = request?.kind === "approval" ? request.requestId : null;
    const pinned =
      request === null ? null : (projected.find((entry) => entry.id === request.key) ?? null);
    const visible = pinned === null ? projected : projected.filter((entry) => entry !== pinned);
    const turnStartId = latestAgentTurnStartId(visible);
    const turnStartIndex =
      turnStartId === null ? null : visible.findIndex((entry) => entry.id === turnStartId);
    const labels = computeSectionLabels(visible, active);
    batch(() => {
      setEntries(reconcile(visible, { key: "id" }));
      setTurnActive(active);
      setKeyboardApprovalId(approvalId);
      // Keep the docked form mounted across transcript updates so its focused field stays focused.
      if (pinnedRequest()?.id !== pinned?.id) {
        setPinnedRequest(pinned);
      }
      setAgentTurnStartId(turnStartId);
      setAgentTurnStartIndex(turnStartIndex);
      setSectionLabels(labels);
      setRevision((value) => value + 1);
    });
    projectedMessages = updates;
  };

  const publish = (updates: AgentPaneUpdate[]): void => {
    batch(() => {
      setMessages(updates);
      if (attached > 0) {
        project(updates);
      }
    });
  };

  return {
    attach() {
      attached += 1;
      if (attached === 1) {
        deactivateHistory = activateHistory();
      }
      const latest = messages();
      if (projectedMessages !== latest) {
        project(latest);
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
    keyboardApprovalId,
    messages,
    pinnedRequest,
    revision,
    sectionLabels,
    session,
    turnActive,
    publish,
    reset() {
      const empty: AgentPaneUpdate[] = [];
      batch(() => {
        setMessages(empty);
        project(empty);
        clearAgentInputDrafts(session);
        setGeneration((value) => value + 1);
      });
    },
  };
}
