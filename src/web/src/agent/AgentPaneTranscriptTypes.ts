import type { AgentPaneUpdate } from "../bridge";
import type { ActivityDetailsSource } from "./AgentActivityDetailsSource";
import type { AgentConversationProjection } from "./AgentConversationProjection";

export type AgentTranscriptTone =
  | "activity"
  | "assistant"
  | "error"
  | "pending"
  | "system"
  | "user"
  | "warning";

export interface AgentActivityStep {
  actionMessage?: AgentPaneUpdate;
  category: string;
  detailText: string | null;
  id: string;
  label: string;
  status: string | null;
  tone: "muted" | "pending" | "running";
}

export interface AgentTranscriptEntry {
  actionMessage: AgentPaneUpdate | null;
  detailCount: number;
  details: AgentActivityStep[];
  activityDetails?: ActivityDetailsSource;
  id: string;
  kind:
    | "updates"
    | "activity"
    | "aside"
    | "block"
    | "message"
    | "thought"
    | "notice"
    | "plan"
    | "request"
    | "subagent"
    | "workflow";
  label: string;
  status: string | null;
  streaming: boolean;
  summary: string | null;
  text: string | null;
  tone: AgentTranscriptTone;
  updatesOpen?: boolean;
  thoughtCollapsed?: true;
  thoughtOpen?: boolean;
  turnStart?: true;
  turnKey?: string | undefined;
  promptTurnId?: string;
  conversation?: AgentConversationProjection;
  asideActive?: boolean | undefined;
  asideCollapsed?: boolean | undefined;
  currentThought?: string | null;
  asideEntries?: readonly AgentTranscriptEntry[];
  pendingEntries?: readonly AgentTranscriptEntry[];
  asideReplyable?: boolean | undefined;
  conversationId?: string;
  subagent?: AgentSubagentInfo | undefined;
  block?: { key: string; kind: "chart" | "table" } | undefined;
}

/** What a subagent card shows beyond its nested transcript. */
export interface AgentSubagentInfo {
  completedAtMs: number | null;
  name: string;
  startedAtMs: number | null;
  state: string;
  task: string | null;
  via: string | null;
}

export type AgentSectionLabel = "Updates" | "Results";

/** One scoped snapshot; unchanged entries retain identity and nested cards link to their own live views. */
export interface AgentTranscriptSnapshot {
  readonly agentTurnStartId: string | null;
  readonly agentTurnStartIndex: number | null;
  readonly currentThought: string | null;
  readonly entries: readonly AgentTranscriptEntry[];
  readonly entryIndexes: ReadonlyMap<string, number>;
  readonly latestResultIndex: number | null;
  readonly pendingRowIndexes: readonly number[];
  readonly sectionLabels: ReadonlyMap<string, AgentSectionLabel>;
  readonly turnActive: boolean;
}
