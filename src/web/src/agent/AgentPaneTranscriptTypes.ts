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
  details: AgentActivityStep[];
  id: string;
  kind: "activity" | "message" | "notice" | "plan" | "request";
  label: string;
  status: string | null;
  summary: string | null;
  text: string | null;
  tone: AgentTranscriptTone;
}
