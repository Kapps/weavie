import type { AgentPaneUpdate } from "../bridge";

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
  tone: "failed" | "muted" | "pending" | "running";
}

export interface AgentTranscriptEntry {
  actionMessage: AgentPaneUpdate | null;
  detailCount: number;
  details: AgentActivityStep[];
  id: string;
  kind: "activity" | "aside" | "message" | "notice" | "plan" | "request";
  label: string;
  status: string | null;
  streaming: boolean;
  summary: string | null;
  text: string | null;
  tone: AgentTranscriptTone;
  turnStart?: true;
  asideActive?: boolean;
  asideEntries?: AgentTranscriptEntry[];
  asideReplyable?: boolean;
  conversationId?: string;
}
