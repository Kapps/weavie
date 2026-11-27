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
  /** The readable branch leaf: `feature/review-front-door` → `review front door`. */
  title: string;
  loaded: boolean;
  providerId: string;
  agentSurface: "terminal" | "structured" | "unavailable";
  agentInputProtocol: number;
  status: SessionStatusName;
  /** What the agent is doing (`running pnpm test`, `wants to edit App.tsx`), or null when only the status applies. */
  activity: string | null;
  /** The session's live workflow run, or null when none is running. */
  run: SessionRun | null;
  /** Why the session's saved workflow run couldn't be loaded, or empty. */
  workflowProblem: string;
  /** When the agent last started or finished work (Unix ms), or null if it never has. */
  lastActiveAt: number | null;
  /** A turn finished here that no one has viewed since — the panel's "Finished" section. */
  unseen: boolean;
  /** Why this session's state can no longer be trusted and it must be reloaded, or null. */
  reloadRequired: string | null;
  /** A worktree Weavie didn't create (another tool or agent made it), so it may still be in use elsewhere. */
  external: boolean;
  /** The root of the repo the session's checkout belongs to. */
  repo: string;
  /** The repo's name; on a rail session, empty unless the workspace's sessions span more than one repo. */
  repoLabel: string;
}

export type SessionRunState =
  | "working"
  | "waitingOnYou"
  | "needsAnswer"
  | "watching"
  | "deciding"
  | "paused"
  | "stalled";

/** A live workflow run as the session list shows it. */
export interface SessionRun {
  id: string;
  workflow: string;
  stage: string;
  state: SessionRunState;
  /** Why a paused run stopped, or the question awaiting the user (and why the model handed it over); empty otherwise. */
  reason: string;
  /** The agent the stage runs on when it differs from the session's own; empty otherwise. */
  agent: string;
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

/** How a device presents agent conversations. */
export interface AgentDisplay {
  /** Where a reply read from its start rests: a fraction of the agent pane's height below its top. */
  replyStartOffset: number;
  thoughtDisplay: "collapsed" | "all";
  progressDisplay: "latest" | "all";
}

export interface AgentProviderInfo {
  id: string;
  name: string;
  available: boolean;
  unavailableReason: string | null;
  warning: string | null;
  surface: "terminal" | "structured";
}

/** The session whose agent sent a prompt to another session. */
export interface AgentMessageSender {
  id: string;
  title: string;
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
  completedAtMs?: number | null;
  itemId?: string | null;
  requestId?: string | null;
  itemType?: string | null;
  itemIds?: string[] | null;
  category?: string | null;
  summary?: string | null;
  text?: string | null;
  status?: string | null;
  /** Whether the agent stopped on this agent message, so it is a reply and not a progress note. */
  final?: boolean | null;
  questions?: AgentInputQuestion[] | null;
  answers?: Record<string, string[]> | null;
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
  planPath?: string | null;
  /** The block a block row shows; its data arrives separately as an `AgentBlockRecord`. */
  blockId?: string | null;
  /** Where in walkthrough `blockId` a side conversation was asked. */
  anchor?: string | null;
  /** The session that sent a submitted prompt, when another session's agent sent it. */
  sender?: AgentMessageSender | null;
}

/** Block fields the host leaves out arrive as `null`. */
/** A JSON scalar in a block: a number, a string, or null. Times are Unix milliseconds. Absent block fields arrive as `null`. */
export type AgentBlockValue = number | string | null;

export interface AgentChartAxis {
  type?: "time" | "category" | "number" | null;
  label?: string | null;
  unit?: string | null;
}

export interface AgentChartSeries {
  name: string;
  style?: "solid" | "comparison" | null;
  axis?: "y" | "y2" | null;
  points: [AgentBlockValue, AgentBlockValue][];
}

export interface AgentChart {
  kind: "line" | "area" | "bar" | "scatter";
  x: AgentChartAxis;
  y?: AgentChartAxis | null;
  y2?: AgentChartAxis | null;
  series: AgentChartSeries[];
  thresholds?: { label?: string | null; axis?: "y" | "y2" | null; value: number }[] | null;
  bands?:
    | {
        label?: string | null;
        axis?: "x" | "y" | "y2" | null;
        from: AgentBlockValue;
        to: AgentBlockValue;
      }[]
    | null;
}

export interface AgentTable {
  columns: { name: string; type: "time" | "number" | "text" | "level" }[];
  rows: AgentBlockValue[][];
}

/** A chart, table or walkthrough an agent shows in its pane; only its latest state exists. */
export interface AgentBlock {
  id: string;
  title: string;
  updatedAtMs?: number | null;
  chart?: AgentChart | null;
  table?: AgentTable | null;
  walkthrough?: AgentWalkthrough | null;
}

/** A guided read of how some code works; its code is anchors into copies the host keeps. */
export interface AgentWalkthrough {
  /** The worktree the cited paths are relative to. */
  root: string;
  problem: string;
  gist: string;
  example?: string | null;
  closing?: string | null;
  change?: string | null;
  stops: WalkthroughStop[];
  files: WalkthroughFile[];
}

/** A cited file: the hash of its first recorded copy and of the copy the last update recorded ("" once gone). */
export interface WalkthroughFile {
  path: string;
  before: string;
  after?: string | null;
  late?: boolean;
}

export interface WalkthroughStop {
  id: string;
  name: string;
  title: string;
  lead: string;
  code: WalkthroughCode[];
  state?: { cells: { label: string; value: string }[]; note?: string | null } | null;
  why?: { title: string; text: string; source: { kind: string; ref?: string | null } } | null;
  predict?: { question: string; answer: string } | null;
  effect?: "changed" | "affected" | "added" | null;
  note?: string | null;
}

/** Lines `from`–`to` of `path`, as the copy `source` held them. */
export interface WalkthroughCode {
  path: string;
  from: number;
  to: number;
  source: string;
  folds?: { from: number; to: number; label: string }[] | null;
  notes?: WalkthroughNote[] | null;
}

export interface WalkthroughNote {
  line: number;
  token: string;
  text: string;
  was?: string | null;
}

/** A block's latest state at the output generation and revision that last changed it. */
export interface AgentBlockRecord {
  generation: number;
  revision: number;
  providerId: string;
  conversationId: string | null;
  block: AgentBlock;
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
}

export interface AgentPaneWireUpdate extends AgentPaneUpdate {
  generation: number;
  ordinal: number;
  revision: number;
  textOffset: number;
  textLength: number;
  /** A history record whose tool output is fetched on expand via `agent.toolOutput`. */
  outputDeferred?: boolean;
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

/** The suggestion profile pickers; each axis id is a setting key and each option id a value for it. */
export interface InferenceChoices {
  axes: AgentControlAxis[];
  warning: string | null;
  error: string | null;
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
      commandId: null;
      inputHint: string | null;
      inputName: null;
    } & ({ kind: "providerCommand" } | { kind: "mcpPrompt" }));

export interface AgentControlState {
  ready: boolean;
  rewindable: boolean;
  axes: AgentControlAxis[];
  slash: AgentSlashEntry[];
}

export interface AgentQueuedSubmission {
  id: string;
  text: string;
  attachments: number;
  sender?: AgentMessageSender | null;
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

/** What the page shows of one repo: its raw test profile, its contextual suggestions, and its recent files. */
export interface RepoState {
  repo: string;
  testProfile: string;
  suggestions: Suggestion[];
  recentFiles: string[];
}

/** A repo a session can be started in, with the host's name for it. */
export interface KnownRepo {
  repo: string;
  label: string;
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
  stickyScrollMaxLines: number;
  fontLigatures: boolean;
  indentGuides: boolean;
  hoverDelay: number;
  suggestExpandDocs: boolean;
  commentProse: CommentProseMode;
  markdownView: "preview" | "source";
  markdownChangeMarks: "current" | "all";
  paneShortcutHints: boolean;
  fullscreenCenter: "window" | "container";
  videoAutoplay: boolean;
  gitBlame: GitBlameMode;
  gitBlameMaxLines: number;
  reviewOverviewCollapse: "global" | "session";
  reviewComments: boolean;
  reviewHints: boolean;
  reviewStepSettled: boolean;
  inlineReplies: boolean;
  /** Files past the ones on screen the unified review mounts while it rests. */
  reviewScreensAhead: number;
  spellCheck: boolean;
  /** The dictionary every spelling request names; the session's host installs one it lacks. */
  spellCheckLocale: string;
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
