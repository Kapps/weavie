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
  providerId: string;
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
  defaultProvider: string;
  providers: AgentProviderInfo[];
}

export interface AgentProviderInfo {
  id: string;
  name: string;
  available: boolean;
  unavailableReason: string | null;
  surface: "terminal" | "structured";
}

export interface AgentPaneUpdate {
  type: string;
  providerId: string;
  threadId?: string | null;
  isPrimaryThread?: boolean | null;
  conversationId?: string | null;
  anchorTurnId?: string | null;
  turnId?: string | null;
  startedAtMs?: number | null;
  itemId?: string | null;
  requestId?: string | null;
  itemType?: string | null;
  itemIds?: string[] | null;
  category?: string | null;
  summary?: string | null;
  text?: string | null;
  status?: string | null;
  questions?: AgentInputQuestion[] | null;
  actions?: AgentActionOption[] | null;
  locations?: AgentPaneLocation[] | null;
  diffs?: AgentPaneDiff[] | null;
  content?: AgentPaneContent[] | null;
  parentItemId?: string | null;
  background?: boolean | null;
  terminalId?: string | null;
  mediaType?: string | null;
  mediaData?: string | null;
  resourceUri?: string | null;
}

export interface AgentPaneContent {
  type: string;
  text?: string | null;
  mediaType?: string | null;
  mediaData?: string | null;
  resourceUri?: string | null;
  name?: string | null;
}

export interface AgentActionOption {
  id: string;
  label: string;
  kind: string;
}

export interface AgentPaneLocation {
  path: string;
  line: number | null;
}

export interface AgentPaneDiff {
  path: string;
  oldText: string | null;
  newText: string;
}

export interface AgentPaneWireUpdate extends AgentPaneUpdate {
  generation: number;
  ordinal: number;
  revision: number;
  textOffset: number;
  textLength: number;
}

export interface AgentPaneHistoryFragment {
  generation: number;
  ordinal: number;
  revision: number;
  jsonOffset: number;
  jsonLength: number;
  json: string;
}

export interface AgentInputQuestion {
  id: string;
  header: string;
  question: string;
  allowsOther: boolean;
  kind: "string" | "number" | "integer" | "boolean" | "array";
  required: boolean;
  format: string | null;
  initialValues: string[];
  minimum: number | null;
  maximum: number | null;
  minimumLength: number | null;
  maximumLength: number | null;
  pattern: string | null;
  options: AgentInputOption[];
}

export interface AgentInputOption {
  value: string;
  label: string;
  description: string;
}

export interface AgentControlOption {
  id: string;
  label: string;
  description: string | null;
  group: string | null;
}

export interface AgentControlAxis {
  id: string;
  label: string;
  description: string | null;
  category: string | null;
  kind: "select" | "boolean";
  value: string;
  valueLabel: string;
  options: AgentControlOption[];
}

interface AgentSlashEntryBase {
  id: string;
  name: string;
  description: string;
}

export type AgentSlashEntry =
  | (AgentSlashEntryBase & {
      kind: "weavieCommand";
      commandId: string;
      inputHint: string | null;
      inputName: string | null;
    })
  | (AgentSlashEntryBase & {
      kind: "providerCommand";
      commandId: null;
      inputHint: string | null;
      inputName: null;
    });

export interface AgentControlState {
  axes: AgentControlAxis[];
  slash: AgentSlashEntry[];
}

export interface AgentQueuedSubmission {
  id: string;
  text: string;
  kind: "prompt" | "providerCommand";
}

export interface AgentContextWindowUsage {
  usedTokens: number;
  capacityTokens: number;
}

export interface AgentUsageLimit {
  id: string;
  status: "allowed" | "warning" | "exhausted";
  usedPercent: number | null;
  resetsAtMs: number | null;
}

export interface AgentUsageSnapshot {
  contextWindow: AgentContextWindowUsage | null;
  limits: AgentUsageLimit[];
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
  mouseWheelScrollSensitivity: number;
  fastScrollSensitivity: number;
  middleClickAutoscroll: boolean;
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
  gitBlame: GitBlameMode;
}

export type CommentProseMode = "none" | "documentation" | "multiline" | "all";

/** Which lines carry the faded blame annotation: none, the cursor's line, or each line starting a commit's run. */
export type GitBlameMode = "off" | "currentLine" | "all";

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
