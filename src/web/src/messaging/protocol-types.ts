import type { OverrideOp } from "../theme/overrides";
import type { VsCodeColorTheme } from "../theme/vscode-theme";

export type ThemeMode = "system" | "light" | "dark";

export interface ThemeSlot {
  id: string;
  ops?: OverrideOp[];
  theme?: VsCodeColorTheme;
}

export type TermSession = "claude" | "shell";

export type SessionStatusName =
  | "starting"
  | "working"
  | "needsInput"
  | "idle"
  | "waiting"
  | "error";

export interface SessionChip {
  id: string;
  label: string;
  loaded: boolean;
  primary: boolean;
  providerId: "claude" | "codex";
  agentSurface: "terminal" | "structured" | "unavailable";
  agentInputProtocol: number;
  status: SessionStatusName;
  hue: number;
  monogram: string;
}

export type AttentionKindName = "turnComplete" | "needsInput" | "failed";

export interface NotificationPrefs {
  sounds: boolean;
  os: boolean;
  volume: number;
  soundPack: string;
  gates: Record<AttentionKindName, boolean>;
}

export interface AgentDefaults {
  defaultProvider: "claude" | "codex";
}

export interface AgentPaneUpdate {
  type: string;
  providerId: "claude" | "codex";
  threadId?: string | null;
  isPrimaryThread?: boolean | null;
  turnId?: string | null;
  startedAtMs?: number | null;
  itemId?: string | null;
  itemType?: string | null;
  category?: string | null;
  summary?: string | null;
  text?: string | null;
  status?: string | null;
  questions?: AgentInputQuestion[] | null;
  payload?: unknown;
}

export interface AgentInputQuestion {
  id: string;
  header: string;
  question: string;
  isSecret: boolean;
  options: AgentInputOption[];
}

export interface AgentInputOption {
  label: string;
  description: string;
}

export interface AgentControlOption {
  id: string;
  label: string;
  description: string | null;
}

export interface AgentControlAxis {
  id: string;
  label: string;
  value: string;
  valueLabel: string;
  options: AgentControlOption[];
  commandId: string | null;
}

export interface AgentModelChoice {
  id: string;
  label: string;
  current: boolean;
  effort: string;
  efforts: AgentControlOption[];
  fastTier: string;
  fastOn: boolean;
}

export interface AgentModelControl {
  value: string;
  valueLabel: string;
  models: AgentModelChoice[];
}

export interface AgentSlashEntry {
  id: string;
  name: string;
  description: string;
  commandId: string | null;
  insertText: string | null;
  skillName: string | null;
}

export interface AgentControlState {
  modelControl: AgentModelControl;
  axes: AgentControlAxis[];
  slash: AgentSlashEntry[];
}

export interface SuggestionAction {
  label: string;
  kind: "RunCommand" | "Snooze" | "DismissForever";
  commandId?: string;
  argsJson?: string;
}

export interface Suggestion {
  id: string;
  title: string;
  body: string;
  actions: SuggestionAction[];
}

export type ResizeEdge =
  | "top"
  | "bottom"
  | "left"
  | "right"
  | "top-left"
  | "top-right"
  | "bottom-left"
  | "bottom-right";

export interface FontSpec {
  family: string;
  size: number;
  weight: string;
}

export interface EditorOptionsSpec {
  inlayHints: "on" | "off" | "offUnlessPressed" | "onUnlessPressed";
  minimap: boolean;
  bracketPairColorization: boolean;
  smoothScrolling: boolean;
  cursorSmoothCaretAnimation: "off" | "on" | "explicit";
  renderWhitespace: "none" | "boundary" | "selection" | "trailing" | "all";
  scrollBeyondLastLine: boolean;
  wordWrap: "off" | "on" | "wordWrapColumn" | "bounded";
  lineNumbers: "on" | "off" | "relative" | "interval";
  cursorBlinking: "blink" | "smooth" | "phase" | "expand" | "solid";
  renderLineHighlight: "none" | "gutter" | "line" | "all";
  stickyScroll: boolean;
  fontLigatures: boolean;
  indentGuides: boolean;
  hoverDelay: number;
  suggestExpandDocs: boolean;
  commentProse: CommentProseMode;
  paneShortcutHints: boolean;
  videoAutoplay: boolean;
}

export type CommentProseMode = "none" | "documentation" | "multiline" | "all";

export interface SearchMatch {
  path: string;
  line: number;
  column: number;
  preview: string;
}

export interface BackendEndpoint {
  bridgeUrl: string;
  resourceBase: string;
}

export interface BackendInfo {
  id: string;
  name: string;
  isLocal: boolean;
}

export interface PullRequestInfo {
  number: number;
  title: string;
  author: string;
  headRef: string;
  url: string;
  draft: boolean;
}

export interface ReviewCommentInfo {
  id: number;
  line: number;
  side: "left" | "right";
  author: string;
  body: string;
  createdAt: string;
  inReplyTo: number;
}
