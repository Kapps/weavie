import { type Accessor, batch, createSignal } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import type { AgentPaneUpdate, ClientSession } from "../bridge";
import { clearAgentInputDrafts } from "./AgentInputDrafts";
import { toAgentTranscript } from "./AgentPaneMessages";
import type { AgentTranscriptEntry } from "./AgentPaneTranscriptTypes";
import { computeSectionLabels, latestAgentTurnStartId } from "./AgentTranscriptLabels";
import { hasActiveTurn, pendingApproval } from "./turn-progress";

export type AgentSectionLabel = "Updates" | "Results";

export interface AgentPaneModel {
  attach(): () => void;
  readonly agentTurnStartId: Accessor<string | null>;
  readonly agentTurnStartIndex: Accessor<number | null>;
  readonly entries: AgentTranscriptEntry[];
  readonly generation: Accessor<number>;
  readonly keyboardApprovalId: Accessor<string | null>;
  readonly messages: Accessor<AgentPaneUpdate[]>;
  readonly revision: Accessor<number>;
  readonly sectionLabels: Accessor<ReadonlyMap<string, AgentSectionLabel>>;
  readonly session: ClientSession;
  readonly turnActive: Accessor<boolean>;
}

export interface MutableAgentPaneModel extends AgentPaneModel {
  publish(updates: AgentPaneUpdate[]): void;
  reset(): void;
}

export function createAgentPaneModel(session: ClientSession): MutableAgentPaneModel {
  const [entries, setEntries] = createStore<AgentTranscriptEntry[]>([]);
  const [messages, setMessages] = createSignal<AgentPaneUpdate[]>([]);
  const [generation, setGeneration] = createSignal(0);
  const [revision, setRevision] = createSignal(0);
  const [turnActive, setTurnActive] = createSignal(false);
  const [keyboardApprovalId, setKeyboardApprovalId] = createSignal<string | null>(null);
  const [agentTurnStartId, setAgentTurnStartId] = createSignal<string | null>(null);
  const [agentTurnStartIndex, setAgentTurnStartIndex] = createSignal<number | null>(null);
  const [sectionLabels, setSectionLabels] = createSignal<ReadonlyMap<string, AgentSectionLabel>>(
    new Map(),
  );
  let attached = 0;
  let projectedMessages: AgentPaneUpdate[] | null = null;

  const project = (updates: AgentPaneUpdate[]): void => {
    const projected = toAgentTranscript(updates);
    const active = hasActiveTurn(updates);
    const approvalId = pendingApproval(updates)?.requestId ?? null;
    const turnStartId = latestAgentTurnStartId(projected);
    const turnStartIndex =
      turnStartId === null ? null : projected.findIndex((entry) => entry.id === turnStartId);
    const labels = computeSectionLabels(projected, active);
    batch(() => {
      setEntries(reconcile(projected, { key: "id" }));
      setTurnActive(active);
      setKeyboardApprovalId(approvalId);
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
      const latest = messages();
      if (projectedMessages !== latest) {
        project(latest);
      }
      return () => {
        attached -= 1;
      };
    },
    agentTurnStartId,
    agentTurnStartIndex,
    entries,
    generation,
    keyboardApprovalId,
    messages,
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
