// Typed JS <-> C# bridge over the WKWebView script-message channel (JS→C# via postMessage, C#→JS via
// window.__weavieReceive). JSON messages; in a plain browser (dev) the host handler is absent and outbound
// is a no-op by design, never a thrown error.

import { createMemo, createSignal } from "solid-js";
import type { CommandInfo, CommandResult, ResolvedKeybinding } from "./commands/types";
import type { EditorSession } from "./editor/session-types";
import type { LayoutDocument } from "./layout/types";
import type { WeavieLspConfig } from "./lsp/lsp-client";
import { notify } from "./notify/notify";
import { ReliableAgentFrames } from "./reliable-agent-frames";
import type { OverrideOp } from "./theme/overrides";
import type { VsCodeColorTheme } from "./theme/vscode-theme";

// Appearance mode: follow the OS (`system`), or force a polarity. `system` resolves against
// `prefers-color-scheme` in the theme controller.
export type ThemeMode = "system" | "light" | "dark";

// One polarity's theme in a theme payload: the selected theme id, its ordered override stack, and —
// for installed themes only — the converted VS Code theme JSON (built-ins resolve by id).
export interface ThemeSlot {
  id: string;
  ops?: OverrideOp[];
  theme?: VsCodeColorTheme;
}

// Which PTY pane within a session: "claude" (the Claude Code TUI) or "shell" (a login shell). Terminal
// messages also carry a `slot` (the session / rail id) to route to the right PTY and xterm.
export type TermSession = "claude" | "shell";

// The live state of a session's embedded Claude, derived host-side from its hook stream + process
// supervisor.
export type SessionStatusName =
  | "starting"
  | "working"
  | "needsInput"
  | "idle"
  // Idle but self-resuming: a scheduled wakeup / background task is pending (holds the update drain).
  | "waiting"
  | "error";

// One session's rail chip (host-pushed in session-list). `hue`/`monogram` are derived deterministically
// from the branch so a session looks the same across restarts. `loaded` is false for a dormant worktree
// (rendered faded; clicking loads it); `status` is only meaningful when loaded. `primary` marks the
// workspace's own checkout, which can't be unloaded or deleted. Loaded chips are ordered first.
export interface SessionChip {
  id: string;
  label: string;
  active: boolean;
  loaded: boolean;
  primary: boolean;
  providerId: "claude" | "codex";
  // Optional for compatibility with an older remote backend. New hosts derive this from provider capabilities.
  agentSurface?: "terminal" | "structured" | "unavailable";
  // Protocol 2 correlates attachment upload and atomic prompt submission. Missing means the legacy remote API.
  agentInputProtocol?: number;
  status: SessionStatusName;
  hue: number;
  monogram: string;
}

// An attention-worthy session event (docs/specs/session-attention.md), mirrored from Core's AttentionKind.
export type AttentionKindName = "turnComplete" | "needsInput" | "failed";

// Resolved notification prefs (the notifications.* settings). Injected as window.__WEAVIE_NOTIFICATIONS__
// before navigation, re-pushed as { type: "notification-prefs" } on change.
export interface NotificationPrefs {
  sounds: boolean;
  os: boolean;
  volume: number;
  soundPack: string;
  /** Per-event gates keyed by the wire kind, so consumers index by an event's kind directly. */
  gates: Record<AttentionKindName, boolean>;
}

export interface AgentPaneUpdate {
  type: string;
  providerId: "claude" | "codex";
  threadId?: string | null;
  turnId?: string | null;
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

// The provider-neutral composer control surface (host-pushed as `agent-controls`, per slot). The web renders
// axes/options and echoes back an axis `id` + option `id` via `agent-set-control`, never learning a provider
// concept. A slash entry either dispatches `commandId` (a built-in action) or inserts `insertText` (a skill).
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
  axes: AgentControlAxis[];
  slash: AgentSlashEntry[];
}

// One button on a contextual-suggestion card. A `RunCommand` action dispatches `commandId` (advertising its
// keybinding); `Snooze`/`DismissForever` send a dismiss-suggestion back to the host.
export interface SuggestionAction {
  label: string;
  kind: "RunCommand" | "Snooze" | "DismissForever";
  commandId?: string;
  argsJson?: string;
}

// A contextual-suggestion card (host-pushed in `suggestions`): a dismissible nudge for the current workspace.
export interface Suggestion {
  id: string;
  title: string;
  body: string;
  actions: SuggestionAction[];
}

// A frameless-window resize edge/corner the user grabbed (Windows custom chrome). The web draws the grab
// handles and names the edge; the host maps it to the matching native resize.
export type ResizeEdge =
  | "top"
  | "bottom"
  | "left"
  | "right"
  | "top-left"
  | "top-right"
  | "bottom-left"
  | "bottom-right";

// Resolved typography for one text surface (editor or terminal). The host injects these as
// window.__WEAVIE_FONTS__ before navigation and re-pushes a { type: "fonts" } message on any change.
export interface FontSpec {
  family: string;
  size: number;
  weight: string;
}

// Resolved editor-behavior options (Monaco IEditorOptions surfaced as Weavie settings). Injected as
// window.__WEAVIE_EDITOR_OPTIONS__ before navigation, re-pushed as { type: "editorOptions" } on change.
// Keys are short camelCase names mapped onto Monaco's nested shape (editor-options.ts); `suggestExpandDocs`
// is the one non-option, a custom behavior Monaco has no setting for.
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
  // Not a Monaco option: toggles the Ctrl+N pane-switch hint badges (see App.tsx).
  paneShortcutHints: boolean;
  // Not a Monaco option: starts playback when a video file opens in the media pane (see MediaPane.tsx).
  videoAutoplay: boolean;
}

/**
 * Which comments the editor renders as prose: `none`; `documentation` (only doc comments `///`/`//!`/`/** *​/`);
 * `multiline` (doc comments plus any spanning ≥2 lines); `all` (every full-line comment).
 */
export type CommentProseMode = "none" | "documentation" | "multiline" | "all";

/** One find-in-files content-search hit: the file's absolute path, 1-based line, and the matched line. */
export interface SearchMatch {
  path: string;
  line: number;
  preview: string;
}

export type HostBoundMessage =
  | { type: "ready" }
  | { type: "monaco-ready" }
  | { type: "log"; level: "info" | "warn" | "error"; message: string }
  // The xterm pane is mounted and ready to host the PTY child. `slot` is the workspace session (rail id)
  // this pane belongs to; `session` is the pane within it.
  | { type: "term-ready"; slot: string; session: TermSession; cols: number; rows: number }
  | { type: "term-input"; slot: string; session: TermSession; dataB64: string }
  // An image pasted into an agent pane: bytes + MIME. The host writes a scratch file and delivers its path through
  // the provider's native image path. `session` is legacy and ignored for routing.
  | { type: "term-paste-image"; slot: string; session: TermSession; mime: string; dataB64: string }
  | { type: "term-resize"; slot: string; session: TermSession; cols: number; rows: number }
  | { type: "agent-attachment-upload"; slot: string; id: string; mime: string; dataB64: string }
  | { type: "agent-attachment-remove"; slot: string; id: string }
  | {
      type: "agent-submit";
      slot: string;
      id?: string;
      prompt: string;
      attachmentIds?: string[];
      skills?: string[];
    }
  | { type: "agent-interrupt"; slot: string }
  | { type: "agent-set-control"; slot: string; axis: string; value: string }
  | { type: "agent-approval"; slot: string; requestId: string; decision: string }
  | { type: "agent-input"; slot: string; requestId: string; answers: Record<string, string[]> }
  // Session rail → host: switch to a session (binds the page to it). Load/unload/delete are weavie.session.*
  // commands run via invoke-command (the delete classify→confirm→delete dance is the `classify` arg + `force`
  // on that one command, not its own messages). See docs/specs/command-responses.md.
  | { type: "switch-session"; id: string }
  // Create a new session. `existing` ⇒ check out the EXISTING `branch` (base ignored); else create a new
  // branch off `base` ("head" = active session's HEAD, or "main"). list-branches asks a backend for its
  // checkout-able branches, answered by a branches-result tagged with the request `id`.
  | {
      type: "new-session";
      branch?: string;
      base?: "head" | "main";
      existing?: boolean;
      agentProviderId?: "claude" | "codex";
    }
  // Dismiss a contextual suggestion: `forever` ⇒ persist ("don't ask again"); else snooze for this run ("not now").
  | { type: "dismiss-suggestion"; id: string; forever: boolean }
  // The user pasted a source's access token into the connect dialog; the host validates + saves it and replies
  // source-token-result tagged with `id`. See docs/specs/notion-source-auth.md.
  | { type: "set-source-token"; id: string; sourceId: string; token: string }
  // Hand an opened URL to the host's open resolver: the host matches it to a source (fetch + render) or replies
  // open-web for a web (iframe) tab. The match (ISource.Match) lives host-side. See docs/specs/notion-source-view.md.
  | { type: "open-target"; url: string }
  // Save one block edit to the source document: an exact-match old/new pair diffed against the verbatim fetched
  // markdown (notion-edit.ts). Answered by a refreshed source-doc, or source-edit-error. See docs/specs/notion-writes.md.
  | { type: "source-save-edit"; target: string; oldStr: string; newStr: string }
  | { type: "list-branches"; id: string }
  // Open PR: list-prs asks a backend for its repo's open pull requests (answered by a prs-result tagged with
  // the request `id`); open-pr checks out the chosen PR's head branch as a session, seeding Claude with its
  // context. See docs/specs/open-pr.md.
  // Browse the repo's PRs: empty `query` → the recent-open default list; a typed query → forge-side search.
  | { type: "list-prs"; id: string; query: string }
  // Open a PR by number (the host resolves its branch refs). `owner`/`repo` are set only for a pasted URL, so
  // the host can refuse one that isn't this workspace's repository.
  | { type: "open-pr"; number: number; owner: string; repo: string }
  // Preview a typed #N / pasted URL: resolve it to a PR (answered by pr-resolved tagged with `id`).
  | { type: "resolve-pr"; id: string; number: number; owner: string; repo: string }
  // Arm a "diff against <ref>" review on the active session; seeds the change tracker so it reviews through the
  // same accept/reject engine as a turn (answered by turn-changes + per-file turn-diff; see diff-against.md).
  | { type: "diff-against"; ref: string }
  // Post a review comment on a PR: a reply when `inReplyTo` is set, else a new comment at `path`/`line`/`side`.
  | {
      type: "add-pr-comment";
      number: number;
      path: string;
      line: number;
      side: "left" | "right";
      inReplyTo: number;
      body: string;
    }
  // IDE-MCP: the user's Keep/Reject decision for an openDiff.
  | { type: "diff-resolved"; id: string; kept: boolean; finalContents: string }
  // Clickable file:line in the terminal -> ask the host to load + reveal the file. `preview` opens it as a
  // reusable preview tab (single-click / go-to-def); omitted/false opens a persistent tab.
  | { type: "reveal-file"; path: string; line: number; preview?: boolean }
  // Terminal copy (an explicit copy command, or Claude's OSC 52) -> write to the OS clipboard via the host,
  // which dodges the WebView clipboard API's focus/permission gate.
  | { type: "clipboard-write"; text: string }
  // Terminal paste -> read the OS clipboard; the host replies with clipboard-content tagged by `id`.
  | { type: "clipboard-read"; id: string }
  // Native-WebView terminal paste can ask the local host for a clipboard image before falling back to text paste.
  | { type: "clipboard-read-image"; id: string }
  // A terminal hyperlink / Claude's OAuth URL -> open in the OS default browser.
  | { type: "open-url"; url: string }
  // The shell child reported its working directory (OSC 7); the host relaunches the shell there on reopen.
  | { type: "term-cwd"; slot: string; session: TermSession; cwd: string }
  // LSP over the bridge: a language client opening (the host spawns `server` for this `slot`, bound to the
  // page-minted `channel`), one JSON-RPC message to the server (payload embedded — already JSON), or closing it.
  | { type: "lsp-start"; slot: string; server: string; channel: string }
  | { type: "lsp-data"; slot: string; channel: string; payload: unknown }
  | { type: "lsp-stop"; slot: string; channel: string }
  // The review walk asks the host for one file's turn diff (review-baseline vs current), so opening a file in
  // the review re-renders its inline applied diff even if its per-file turn-diff push was missed.
  | { type: "get-turn-diff"; path: string }
  // Host-backed file:// provider: the editor's VSCode working copies read/write disk through the host.
  // Each request carries an `id` the host echoes on the matching fs-*-result, correlating the reply.
  | { type: "fs-stat"; id: string; path: string }
  | { type: "fs-read"; id: string; path: string }
  // Raw bytes (base64) for the media pane's image/video render — same confinement + correlation as fs-read.
  | { type: "fs-read-bytes"; id: string; path: string }
  | { type: "fs-write"; id: string; path: string; content: string }
  // Inline diff (acceptEdits mode): accept the whole turn's changes — clears the inline markers. The host
  // snapshots the per-turn baseline to current and re-pushes an (empty) turn diff.
  | { type: "accept-turn" }
  // Inline diff (acceptEdits mode): undo the whole turn's changes — the host reverts each touched file to its
  // turn baseline on disk and live-refreshes the editor.
  | { type: "undo-turn" }
  // Inline review: revert ALL of one file's changes back to its turn baseline on disk (the file-scoped
  // analogue of undo-turn). The host restores the file wholesale and live-refreshes the editor.
  | { type: "revert-file"; path: string }
  // Inline review (auto-keep modes): revert ONE hunk on disk; the host splices its own baseline lines back
  // over the current ones. Ranges are 1-based, end-exclusive. `guardText` is the web's current text of
  // [currentStart, currentEndExclusive) — an optimistic-concurrency check; a mismatch aborts and re-emits.
  | {
      type: "reject-hunk";
      path: string;
      baselineStart: number;
      baselineEndExclusive: number;
      currentStart: number;
      currentEndExclusive: number;
      guardText: string;
    }
  // Inline review: KEEP ONE hunk — advance the host's review baseline over it (no disk write) so it drops from
  // the pending diff for good and survives session switches. Same ranges + `guardText` shape as reject-hunk.
  | {
      type: "keep-hunk";
      path: string;
      baselineStart: number;
      baselineEndExclusive: number;
      currentStart: number;
      currentEndExclusive: number;
      guardText: string;
    }
  // Inline review: KEEP ALL of one file's changes — advance its review baseline to current (no disk write) so the
  // file leaves the review set for good (the file-scoped analogue of accept-turn).
  | { type: "keep-file"; path: string }
  // Inline review: UN-KEEP one faded (accepted) hunk — Core splices the accepted-anchor lines back into the review
  // baseline so it returns to the bright pending band (no disk write). `accepted*` is the range in the accepted
  // anchor (the restored lines); `review*` the range in the review baseline (the splice target). Both sides carry
  // the rendered text as a guard (`acceptedGuardText` / `guardText`); the host aborts if either moved.
  | {
      type: "unkeep-hunk";
      path: string;
      acceptedStart: number;
      acceptedEndExclusive: number;
      reviewStart: number;
      reviewEndExclusive: number;
      acceptedGuardText: string;
      guardText: string;
    }
  // Review undo/redo. `kind` "keep" undoes the last keep, "revert" the last revert (the type-split chords);
  // omitted, the most recent action of either kind (the toolbar's generic Undo). Redo re-applies the last undone.
  | { type: "review-undo"; kind?: "keep" | "revert" }
  | { type: "review-redo" }
  // The file browser asks the host to list a directory under the session root (root when path is "").
  | { type: "list-dir"; path: string }
  // The user changed the pane layout (split ratio, active pane); host persists + reconciles it.
  | { type: "layout-changed"; document: LayoutDocument }
  // The editor session changed (debounced; host persists it). Carries open-list + active + per-file view
  // state, NEVER file contents. `sessionId` stamps the owning session; the host drops a change whose id
  // isn't the active session, so a stale debounced write can't leak one worktree's tabs into another.
  | { type: "editor-session-changed"; sessionId: string | null; session: EditorSession }
  // New File (Ctrl+N): ask the host to create a fresh scratch buffer (an "Untitled-N" temp file in the
  // workspace scratch dir) and push it back as an open-file with `scratch: true`.
  | { type: "new-scratch" }
  // Save a scratch buffer under a real name: the host opens a Save dialog (default `suggestedName`), writes
  // `content` to the chosen path, deletes the temp file, and replies with `scratch-saved`. `path` is the temp path.
  | { type: "save-scratch-as"; path: string; content: string; suggestedName: string }
  // Save a scratch buffer under a name chosen by the in-app prompt (browser-served host, no native dialog): the
  // host resolves `name` under the workspace root, writes `content`, deletes the temp, and replies scratch-saved.
  | { type: "save-scratch-named"; path: string; content: string; name: string }
  // Discard a scratch buffer the user closed: delete its temp file. (The web has already dropped the tab.)
  | { type: "discard-scratch"; path: string }
  // The editor's active file or selection changed -> host updates the editor store, which tells the
  // embedded Claude what the user is looking at (selection_changed). Positions are 0-based.
  | {
      type: "active-editor-changed";
      uri: string;
      languageId: string;
      text: string;
      // The session id whose editor this is (from the last set-editor-session); the host rejects a stale
      // post-switch emit (id != active session) so a background session's Claude isn't told the wrong file.
      sessionId: string | null;
      selection: {
        start: { line: number; character: number };
        end: { line: number; character: number };
        isEmpty: boolean;
      };
    }
  // The open editor tab set changed -> host updates the editor store so Claude's getOpenEditors is accurate.
  // `path` is the web's tab key (a native path); the host derives uri/label and echoes it on close-tab. No content.
  | {
      type: "open-editors-changed";
      // The session id these tabs belong to (from the last set-editor-session); host rejects a stale send.
      sessionId: string | null;
      editors: { path: string; isActive: boolean; isPinned: boolean; isPreview: boolean }[];
    }
  // Custom title bar (Windows): the min / maximize-restore / close buttons.
  | { type: "window-control"; action: "minimize" | "maximize-toggle" | "close" }
  // Frameless-window resize: the user grabbed an edge/corner handle -> host begins a native OS resize.
  // The WebView2 covers the host's real resize border, so resize can't come from the native frame.
  | { type: "window-resize"; edge: ResizeEdge }
  // Custom title bar File menu: open a folder, open a recent (carries its path), close this window, quit.
  | {
      type: "menu-action";
      action: "open-folder" | "open-recent" | "close-window" | "exit";
      path?: string;
    }
  // The omnibar asks the host to (re)send the workspace's flat file list for "Go to File".
  | { type: "request-file-index" }
  // Find-in-files: search the active session's worktree contents (git grep); the host replies with
  // find-in-files-results echoing the query. An empty query clears results without running git.
  | { type: "find-in-files"; query: string }
  // Remote-agent registry (host persists, web connects). add/remove persist/forget an agent. Both target the
  // local backend, since the registry is a local-machine concept. See remote-agents.ts.
  | { type: "add-remote-agent"; name: string; url: string; token: string }
  | { type: "remove-remote-agent"; name: string }
  // Session rail UI state (host-persisted in ~/.weavie/rail-state.json; both target the local backend).
  // set-last-location remembers where the last session was created; set-promoted carries the promoted set.
  | { type: "set-last-location"; location: string }
  | { type: "set-promoted"; promoted: string[] }
  // A keybinding/palette/menu invoked a Core command. A `token` requests a command-result reply
  // (request/response); without one the host runs it fire-and-forget.
  | { type: "invoke-command"; id: string; args?: unknown; token?: string }
  // Reply to a host run-command: whether the web handler ran (Claude's runCommand of a web command).
  | { type: "command-ack"; token: string; ok: boolean; error?: string };

export type WebBoundMessage =
  // `replay: true` marks reattach-synthesized bytes (scrollback replay, mode restore): the pane must not let
  // xterm's answers to device queries inside them (DSR/DA…) reach the child — they'd land as garbage input.
  | { type: "term-output"; slot: string; session: TermSession; dataB64: string; replay?: boolean }
  | { type: "term-exit"; slot: string; session: TermSession; code: number }
  // Host asks this pane to reset + re-emit term-ready. The sole caller is a deliberate child relaunch (shell
  // setting changed), so `respawn` is true for a full reset. Session switches don't reset (pure show/hide).
  | { type: "term-reset"; slot: string; session: TermSession; respawn: boolean }
  | { type: "agent-pane"; slot: string; workspace: string; message: AgentPaneUpdate }
  | { type: "agent-pane-reset"; slot: string; workspace: string }
  | { type: "agent-controls"; slot: string; workspace: string; state: AgentControlState }
  | {
      type: "agent-attachment-state";
      slot: string;
      id: string;
      status: "ready" | "failed" | "removed";
      error: string;
    }
  | {
      type: "agent-submission-state";
      slot: string;
      id: string;
      attachmentIds: string[];
      status: "accepted" | "rejected";
      error: string;
    }
  // Host pushes a session's Claude status (derived from its hook stream + process supervisor).
  | { type: "session-status"; session: TermSession; status: SessionStatusName }
  // Host pushes the active session's git branch + dirty flag for the terminal-column footer (active-backend
  // gated like the page's other state). `branch` is null when the workspace isn't a git repo / HEAD is detached.
  | { type: "git-status"; branch: string | null; dirty: boolean }
  // Host pushes the forge URL prefix a terminal `#N` reference links to (e.g. `https://github.com/owner/repo/pull/`)
  // for the active session's origin; null when it isn't a forge repo (so `#N` stays plain text). See ref-link-store.
  | { type: "ref-link-base"; prefix: string | null }
  // Host pushes the full session list for the rail (id, label, active, status, deterministic identity).
  | { type: "session-list"; sessions: SessionChip[] }
  // A session wants attention (turn complete / needs input / crashed), with its rail identity. Pushed by
  // every backend, never active-gated, so background/remote pings reach the client (session-attention.md).
  | { type: "session-attention"; slot: string; label: string; kind: AttentionKindName }
  // Host pushes the active contextual suggestions (dismissible nudge cards). Ambient — fanned out per backend.
  | { type: "suggestions"; items: Suggestion[] }
  // Host asks the web to move keyboard focus into a pane (kind, e.g. "terminal:claude") — pushed after a
  // session switch so a new / selected session lands focus in the agent.
  | { type: "focus-pane"; kind: string }
  // Connect a source: the host opened its token page in the browser; show the dialog to paste the token, which
  // goes back as set-source-token. See docs/specs/notion-source-auth.md.
  | { type: "prompt-source-token"; sourceId: string; label: string }
  // Result of validating + saving a pasted token (tagged with the request `id`): ok closes the dialog, else the
  // dialog shows `error` inline so the user can correct the token in place.
  | { type: "source-token-result"; id: string; ok: boolean; error: string }
  // A source fetch started (keyed by `target`): open the source tab immediately with `title` and a spinner, so a
  // slow Notion fetch shows progress instead of a frozen window. `sourceId` is the producing source's stable id
  // (ISource.Id, or the log viewer's "logs") — the tab icon keys off it. `source-doc`/`source-error` follow.
  | { type: "source-loading"; target: string; title: string; sourceId: string }
  // A fetched source doc keyed by `target`, carrying exactly one body: `markdown` (a Notion page — SourceView
  // renders it to HTML) or pre-rendered `html` (the host's log viewer; SourceView re-sanitizes it). `editedTime`
  // (ISO, may be "") heads the rendered page. `truncated`/`unknownBlocks` flag content the source couldn't return
  // (rendered as a banner; absent from html-bodied docs). Feeds the kind:"source" tab source-loading opened.
  | {
      type: "source-doc";
      target: string;
      title: string;
      markdown?: string;
      html?: string;
      editedTime: string;
      sourceId: string;
      truncated?: boolean;
      unknownBlocks?: number;
    }
  // A source fetch failed (keyed by `target`): the open tab swaps its spinner for the error reason.
  | { type: "source-error"; target: string; message: string }
  // A source-save-edit failed (keyed by `target`): shown inline at the edited block. `stale` means the page
  // changed in Notion since the fetch (the exact-match op missed), so the block offers a re-fetch.
  | { type: "source-edit-error"; target: string; message: string; stale: boolean }
  // The host's open resolver decided the URL isn't a source — open it as a web (iframe) tab.
  | { type: "open-web"; url: string }
  // IDE-MCP openDiff arriving from Claude: render an editable Monaco diff.
  | {
      type: "show-diff";
      id: string;
      path: string;
      tabName: string;
      original: string;
      proposed: string;
    }
  | { type: "close-diff"; id: string }
  // Reply to clipboard-read (terminal paste), correlated by `id`: the OS clipboard's text ("" when empty).
  | { type: "clipboard-content"; id: string; text: string }
  // Reply to clipboard-read-image (claude-pane paste), correlated by `id`: the OS clipboard's image (base64 +
  // MIME), or an empty `mime` when it holds no image.
  | { type: "clipboard-image-content"; id: string; mime: string; dataB64: string }
  // Host delivers a file to load + reveal (a Monaco working copy, or the media pane for images/video).
  // `preview` ⇒ reusable preview tab, else persistent. `scratch` marks an untitled buffer (New File /
  // restored). No content rides along — the web reads disk through the fs provider.
  | {
      type: "open-file";
      path: string;
      line: number;
      preview?: boolean;
      scratch?: boolean;
    }
  // Reply to save-scratch-as. `savedPath` is the chosen target ("" if cancelled). `reopen` is true when it's
  // inside the workspace (editor reopens it as a working copy); false otherwise (editor drops the scratch tab).
  | { type: "scratch-saved"; scratchPath: string; savedPath: string; reopen: boolean }
  // Host (driven by Claude's close_tab MCP tool) asks the web to close the tab for this file path.
  | { type: "close-tab"; path: string }
  // Host pushes the persisted/reconciled layout (on startup, and after any layout-changed or MCP edit).
  | { type: "set-layout"; document: LayoutDocument }
  // Host pushes the persisted editor session to restore (launch/Ctrl+R + every switch). NO file content —
  // the web reopens each file as a working copy from disk. `sessionId` is the owning session, which the web
  // echoes on the *-changed messages so a post-switch send is attributed correctly.
  | { type: "set-editor-session"; sessionId: string | null; session: EditorSession }
  // Host pushes resolved fonts when a font setting changes (ApplyMode.Live); applied to editor + terminal.
  | { type: "fonts"; editor: FontSpec; terminal: FontSpec }
  // Host re-pushes the resolved notification prefs when a notifications.* setting changes (ApplyMode.Live).
  // A local-machine push: one prefs source (the page-serving backend) governs presentation.
  | ({ type: "notification-prefs" } & NotificationPrefs)
  // Host pushes resolved editor options when an editor.* setting changes (ApplyMode.Live); applied via
  // editor.updateOptions (plus the suggest-docs custom behavior).
  | { type: "editorOptions"; options: EditorOptionsSpec }
  // Host pushes the workspace's test profile (raw test.profile JSON, "" when unconfigured) so the run-lens
  // provider refreshes; injected pre-nav as window.__WEAVIE_TEST_PROFILE__ and re-pushed on change.
  | { type: "test-profile"; profile: string }
  // Host pushes the appearance mode + both polarities' themes, so the web resolves `system` against the live
  // OS setting and switches light↔dark instantly. Re-themes editor, terminal, chrome live.
  | { type: "theme"; mode: ThemeMode; light: ThemeSlot; dark: ThemeSlot }
  // file:// provider replies, correlated by `id`: fs-stat-result (existence + stat); fs-read-result (content
  // + etag, or code:"FileNotFound" / error); fs-write-result (post-write etag, or error). Absent ⇒ N/A.
  | {
      type: "fs-stat-result";
      id: string;
      ok: boolean;
      exists: boolean;
      isDir: boolean;
      mtimeMs: number;
      ctimeMs: number;
      size: number;
      error?: string;
    }
  | {
      type: "fs-read-result";
      id: string;
      ok: boolean;
      content?: string;
      mtimeMs?: number;
      size?: number;
      code?: string;
      error?: string;
    }
  // Reply to fs-read-bytes, correlated by `id`: the file's raw bytes as base64 (or code:"FileNotFound" / error).
  | {
      type: "fs-read-bytes-result";
      id: string;
      ok: boolean;
      dataB64?: string;
      mtimeMs?: number;
      size?: number;
      code?: string;
      error?: string;
    }
  | {
      type: "fs-write-result";
      id: string;
      ok: boolean;
      mtimeMs?: number;
      size?: number;
      error?: string;
    }
  // The host-backed file:// provider learned files changed on disk (a Claude edit, or the workspace watcher
  // catching an external edit): fire the provider's change event so VSCode reloads the affected working copies.
  | { type: "fs-change"; changes: { path: string; kind: "updated" | "added" | "deleted" }[] }
  // The per-TURN change list (files + each file's first-change line), driving the inline review walk's
  // ← / → axis and the parked navigator (which surfaces the review without moving the editor). Pushed live as
  // changes land; empty after a turn boundary / switch. `label` names a PR/ref review ("PR #12", "vs main") in
  // the toolbar/parked subtitle when this set was armed from a ref (a PR or "diff against"); empty for a plain turn.
  | {
      type: "turn-changes";
      label: string;
      files: { path: string; name: string; added: number; removed: number; line: number }[];
    }
  // One file's per-TURN diff as the (acceptedBaseline, baseline, current) triple: baseline→current is the bright
  // pending band, acceptedBaseline→baseline the faded accepted band. `acceptedBaseline === current` means "no
  // markers"; `baseline === current` means "faded accepted only" (every hunk kept but not yet committed).
  | {
      type: "turn-diff";
      path: string;
      name: string;
      acceptedBaseline: string;
      baseline: string;
      current: string;
    }
  // A turn boundary: clear all inline turn markers (the prior turn is implicitly accepted).
  | { type: "turn-reset" }
  // Review undo/redo availability, so the page enables its Undo/Redo affordances and lets the type-split undo
  // chords decline (fall through) when there's nothing of that kind to undo. `canUndo` is "either kind".
  | {
      type: "review-history";
      canUndo: boolean;
      canUndoKeep: boolean;
      canUndoRevert: boolean;
      canRedo: boolean;
    }
  // A session switch: re-point the editor's language clients at the incoming session's worktree (its own root +
  // slot to tag frames with). Handled by rebindLanguageServices (lsp/lsp-client.ts).
  | { type: "lsp-config"; config: WeavieLspConfig }
  // One JSON-RPC frame from a language server (demuxed by `channel`), or its exit/failure-to-start (`reason`
  // carries the host-side cause, e.g. "no server on PATH"). Routed to lsp-bridge-transport.ts.
  | { type: "lsp-data"; slot: string; channel: string; payload: unknown }
  | { type: "lsp-exit"; slot: string; channel: string; code: number; reason?: string }
  // A user-facing notification to surface as a toast (e.g. an autosave write that failed — the user must
  // see that their work didn't reach disk, never a silent drop).
  // `key` (optional) dedupes: a later toast with the same key replaces the live one (e.g. a "settings reloaded"
  // info clearing the lingering "settings malformed" error).
  | { type: "notify"; level: "error" | "warn" | "info"; message: string; key?: string }
  | { type: "notify-clear"; key: string }
  // Update drain state (docs/specs/runner-auto-update.md): what's holding a pending update restart
  // (re-pushed on every change, and on `ready` for a tab that connected mid-drain)…
  | {
      type: "update-pending";
      holds: {
        session: string;
        reason: "working" | "needs-input" | "shell-job" | "waiting-on-task";
      }[];
    }
  // …and the moment the restart commits: input is frozen host-side and the worker is about to exit;
  // the page shows the blocking "Updating…" overlay until the new worker is back.
  | { type: "update-restarting" }
  // The worker's build identity, pushed first on every `ready` cycle. A tab that reconnected to a
  // worker updated under it sees a different build than its boot-time __WEAVIE_SHELL__.buildNumber
  // and reloads itself to pick up the matching assets.
  | { type: "host-info"; buildNumber: string }
  // Host answers list-dir with a directory's entries (directories first), each with an absolute path.
  | {
      type: "dir-listing";
      path: string;
      entries: { name: string; path: string; isDir: boolean }[];
    }
  // Host pushes the window's chrome state so the title bar updates its maximize glyph and blur dim.
  | { type: "window-state"; maximized: boolean; focused: boolean }
  // Host answers request-file-index with the workspace root + every file's absolute path (for the omnibar).
  // `pending` = a session switch invalidated the index and the new worktree's walk is still running: files is
  // empty and the omnibar shows a loading state instead of claiming the worktree has no files.
  | { type: "file-index"; root: string; files: string[]; pending?: boolean }
  // Host answers find-in-files with the content-search matches, echoing the `query` so the page can drop a
  // stale reply. `truncated` ⇒ the match cap was hit and the list is incomplete (surfaced in the panel).
  // `error` ⇒ the git search failed (e.g. git unavailable); the panel shows it rather than "No results".
  | {
      type: "find-in-files-results";
      query: string;
      matches: SearchMatch[];
      truncated: boolean;
      error?: string;
    }
  // Host pushes the recently-used files (most-frecent-first absolute paths) for the omnibar's Recent section.
  // Routed cross-backend (isSessionMessage) and kept per backend, so a remote session shows the remote box's
  // files — whose paths only make sense there — not the local machine's.
  | { type: "recent-files"; files: string[] }
  // Host answers list-branches with the local branches available to check out, tagged by the request `id`.
  // Routed cross-backend (isSessionMessage) so the New Session dialog can query a non-active backend.
  | { type: "branches-result"; id: string; branches: string[] }
  // Host answers list-prs with the repo's open pull requests, tagged by the request `id` (cross-backend, like
  // branches-result).
  | { type: "prs-result"; id: string; prs: PullRequestInfo[] }
  // Host answers resolve-pr with the single PR (or null when it doesn't exist / is a foreign repo), tagged by `id`.
  | { type: "pr-resolved"; id: string; pr: PullRequestInfo | null }
  // A PR file's review comments, pushed alongside its turn-diff so the inline diff anchors threads on it and shows
  // the Comment button. Keyed by absolute `path`; `number` is the PR the comments belong to (carried back on
  // add-pr-comment). An empty list still marks the file as a PR file (commenting enabled); a local "diff against"
  // ref pushes none (no forge behind it → no comment affordance). See docs/specs/diff-against.md.
  | { type: "review-comments"; number: number; path: string; comments: ReviewCommentInfo[] }
  // Host pushes the command catalog + resolved keybindings (on a live ~/.weavie/keybindings.json edit).
  | { type: "commands"; commands: CommandInfo[]; keybindings: ResolvedKeybinding[] }
  // Host pushes the persisted remote-agent registry (on `ready` + any add/remove); the web reconciles its
  // connections. Honored only from the local backend, so a remote runner's push is ignored. See remote-agents.ts.
  | { type: "remote-agents"; agents: { name: string; url: string; token: string }[] }
  // Host pushes the persisted session-rail UI state (on `ready` and on any change, from this or another
  // window). Honored only from the local backend. See rail-state.ts.
  | { type: "rail-state"; lastLocation: string; promoted: string[] }
  // Host asks the web to run a web command Claude invoked over MCP; the web replies with command-ack.
  | { type: "run-command"; id: string; args?: unknown; token: string }
  // Reply to a tokened invoke-command: the command's outcome, routed back to the issuing client by `token`
  // (never gated by the active backend). `data` is the command's optional payload. See command-responses.md.
  | {
      type: "command-result";
      token: string;
      ok: boolean;
      message?: string;
      error?: string;
      data?: unknown;
    };

type WebMessageHandler = (msg: WebBoundMessage) => void;
type SessionMessageHandler = (msg: WebBoundMessage, backendId: string) => void;

// Listeners that render the page — they only ever see the ACTIVE backend's traffic, so a background
// backend can never paint over what's on screen.
const listeners = new Set<WebMessageHandler>();
// Listeners for the cross-backend rail: session-list / session-status from EVERY connected backend, tagged
// with their origin backend, so the rail can show local + remote sessions side by side.
const sessionListeners = new Set<SessionMessageHandler>();

// The id of the backend whose traffic drives the page. "local" is the default backend (the native shell's
// in-process host, or the same-origin headless WebSocket); remotes are added via connectBackend.
const [activeBackend, setActiveBackend] = createSignal("local");
/** The default backend's id — the machine the user is at (the native shell, or the same-origin headless host). */
export const LOCAL_BACKEND_ID = "local";

// The live link state of a backend's transport: opening for the first time, connected, or dropped and
// retrying. The native in-process backend has no entry and is treated as always online. Drives the
// reconnecting banner over the active panes; transitions also raise the one-shot connection toasts.
export type BackendPhase = "connecting" | "online" | "reconnecting";
const [phases, setPhases] = createSignal<Map<string, BackendPhase>>(new Map());
function setBackendPhase(id: string, phase: BackendPhase): void {
  setPhases((prev) => {
    if (prev.get(id) === phase) {
      return prev;
    }
    const next = new Map(prev);
    next.set(id, phase);
    return next;
  });
}
function clearBackendPhase(id: string): void {
  setPhases((prev) => {
    if (!prev.has(id)) {
      return prev;
    }
    const next = new Map(prev);
    next.delete(id);
    return next;
  });
}

/** The live link state of the backend currently driving the page (online unless its socket is opening/retrying). */
export const activeBackendPhase = createMemo<BackendPhase>(
  () => phases().get(activeBackend()) ?? "online",
);
/** True while the active backend's link is down (opening or reconnecting) — the panes can't reach their host. */
export const activeBackendOffline = createMemo<boolean>(() => activeBackendPhase() !== "online");

// These route cross-backend (tagged with origin) rather than being dropped by the active-backend gate — they
// feed the rail, the New Session typeahead, the active backend's suggestions/recent-files, and local-only
// registry/rail state. Everything else is gated to the bound backend.
function isSessionMessage(type: string): boolean {
  return (
    type === "session-list" ||
    type === "session-status" ||
    type === "session-attention" ||
    type === "agent-attachment-state" ||
    type === "agent-submission-state" ||
    type === "suggestions" ||
    type === "recent-files" ||
    type === "branches-result" ||
    type === "prs-result" ||
    type === "pr-resolved" ||
    type === "remote-agents" ||
    type === "rail-state"
  );
}

// A command awaiting its result, keyed by token, tagged with the backend it was sent to (so a disconnect can
// fail its in-flight commands rather than hang them). See invokeCommandOnBackend / command-result.
const pendingCommands = new Map<
  string,
  { resolve: (result: CommandResult) => void; backendId: string }
>();
let commandSeq = 0;

/**
 * Run a Core command on a specific backend and await its result (request/response). The host replies with a
 * `command-result` tagged by token; correlation makes it unicast even over a shared transport. Always resolves
 * (failures come back as `{ ok: false, error }`) — including when the backend disconnects mid-flight.
 */
export function invokeCommandOnBackend(
  backendId: string,
  id: string,
  args: unknown,
): Promise<CommandResult> {
  const token = `c${++commandSeq}`;
  return new Promise<CommandResult>((resolve) => {
    pendingCommands.set(token, { resolve, backendId });
    postToBackend(backendId, { type: "invoke-command", id, args, token });
  });
}

// Fail every command still awaiting a backend whose link dropped, so a lost reply surfaces as an error instead
// of hanging forever (no silent fallback). The transport reconnects for future commands.
function failPendingForBackend(backendId: string, reason: string): void {
  for (const [token, pending] of pendingCommands) {
    if (pending.backendId === backendId) {
      pendingCommands.delete(token);
      pending.resolve({ ok: false, error: reason });
    }
  }
}

// Parse one inbound JSON line and route it: a command-result resolves its awaiting caller by token (never
// gated); session messages fan out to the rail listeners tagged with `backendId`; all else reaches page
// listeners only from the active backend. Shared by every transport.
function deliverFromHost(raw: string, backendId: string): void {
  let parsed: WebBoundMessage;
  try {
    parsed = JSON.parse(raw) as WebBoundMessage;
  } catch {
    log("error", `bridge: bad JSON from ${backendId}: ${raw.slice(0, 200)}`);
    return;
  }
  if (parsed.type === "command-result") {
    const pending = pendingCommands.get(parsed.token);
    if (pending !== undefined) {
      pendingCommands.delete(parsed.token);
      pending.resolve(parsed);
    }
    return;
  }
  if (isSessionMessage(parsed.type)) {
    for (const listener of sessionListeners) {
      listener(parsed, backendId);
    }
    return;
  }
  // Local-machine pushes (the OS clipboard reply, the native window state) route from the local backend
  // only, whichever backend drives the page; everything else is gated to the active backend.
  const localMachinePush =
    parsed.type === "clipboard-content" ||
    parsed.type === "clipboard-image-content" ||
    parsed.type === "window-state" ||
    parsed.type === "notification-prefs";
  if (localMachinePush ? backendId !== LOCAL_BACKEND_ID : backendId !== activeBackend()) {
    return;
  }
  for (const listener of listeners) {
    listener(parsed);
  }
}

// One way to push bytes to a backend (the bridge speaks the same JSON over every transport; only the pipe
// differs). `dispose` tears the pipe down for good so it stops reconnecting.
interface BridgeTransport {
  send(json: string): void;
  dispose(): void;
}

// The native desktop shells' in-process script-message channel (inject `window.webkit.messageHandlers.weavie`
// + call `window.__weavieReceive`). Best-effort: a throwing channel must never break the app.
const nativeTransport: BridgeTransport = {
  send(json: string): void {
    const handler = window.webkit?.messageHandlers?.weavie;
    if (handler === undefined) {
      return;
    }
    try {
      handler.postMessage(json);
    } catch {
      // Best-effort; never let instrumentation break the app.
    }
  },
  // The in-process local backend is never disconnected, so tearing it down is a no-op.
  dispose(): void {},
};

// The `ready` announcement that makes a host (re-)push its state — sent on every remote (re)connect and on a
// local reconnect (main.tsx sends the local backend's initial one).
const READY_HELLO = JSON.stringify({ type: "ready" });

// Remote/web Weavie: a headless "serve" host exposes the same bridge protocol over a WebSocket. Outbound
// before the socket opens is buffered and flushed on open; a dropped socket reconnects with capped backoff.
// Inbound frames go through the shared `deliverFromHost`, indistinguishable from a native host.
class WebSocketTransport implements BridgeTransport {
  private socket: WebSocket | null = null;
  private readonly outbox: string[] = [];
  private readonly reliableAgentFrames = new ReliableAgentFrames();
  private reconnectDelayMs = 500;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  // True once a connect has succeeded, so a later open is a reconnect and must re-announce readiness.
  private hasOpened = false;
  // Set once the backend is deliberately disconnected; stops the close→reconnect loop for good.
  private disposed = false;
  // Mirrors the published phase so a drop can decide its one-shot toast without a reactive read.
  private phase: BackendPhase = "connecting";
  // Dedupe key shared by this backend's connection toasts, so a "Reconnected" info replaces the lingering
  // "Lost connection" error in place rather than stacking on top of it (issue #136).
  private get connectionToastKey(): string {
    return `connection:${this.backendId}`;
  }

  constructor(
    private readonly backendId: string,
    private readonly url: string,
    // Human label for connection toasts/banner ("the Weavie host" for local, the agent name for a remote).
    private readonly label: string,
    // Re-sent on every (re)connect, so a backend re-pushes its state after a dropped link. Remotes pass
    // `ready`; the local backend leaves it undefined (main.tsx sends its initial ready).
    private readonly hello?: string,
  ) {
    setBackendPhase(this.backendId, "connecting");
    this.connect();
  }

  send(json: string): void {
    this.reliableAgentFrames.track(json);
    if (this.socket !== null && this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(json);
      return;
    }
    this.outbox.push(json);
  }

  /** Disconnect for good: stop reconnecting and close the socket (the user removed this remote backend). */
  dispose(): void {
    this.disposed = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.socket?.close();
    this.socket = null;
    clearBackendPhase(this.backendId);
  }

  private connect(): void {
    if (this.disposed) {
      return;
    }
    let socket: WebSocket;
    try {
      socket = new WebSocket(this.url);
    } catch {
      this.onDrop();
      return;
    }
    this.socket = socket;
    socket.onopen = (): void => {
      this.reconnectDelayMs = 500;
      if (this.phase === "reconnecting") {
        notify("info", `Reconnected to ${this.label}.`, this.connectionToastKey);
      }
      this.phase = "online";
      setBackendPhase(this.backendId, "online");
      if (this.hello !== undefined) {
        socket.send(this.hello);
      } else if (this.hasOpened) {
        // The local backend's initial `ready` came from main.tsx; the host re-pushes state (and re-syncs
        // terminals) only on `ready`, so a reconnect re-announces it here (remotes' `hello` already does).
        socket.send(READY_HELLO);
      }
      this.hasOpened = true;
      const pending = this.outbox.splice(0, this.outbox.length);
      for (const message of pending) {
        socket.send(message);
      }
      const pendingSet = new Set(pending);
      for (const message of this.reliableAgentFrames.replay()) {
        if (!pendingSet.has(message)) {
          socket.send(message);
        }
      }
    };
    socket.onmessage = (event: MessageEvent): void => {
      if (typeof event.data === "string") {
        this.reliableAgentFrames.acknowledge(event.data);
        deliverFromHost(event.data, this.backendId);
      }
    };
    socket.onclose = (): void => {
      this.socket = null;
      // The link dropped: fail any commands awaiting this backend's reply rather than leave them hanging.
      failPendingForBackend(
        this.backendId,
        "The backend disconnected before the command completed.",
      );
      if (!this.disposed) {
        this.onDrop();
      }
    };
    socket.onerror = (): void => {
      // onerror is always followed by onclose, which drives the reconnect. Close defensively so a
      // transport blip never surfaces as an uncaught error.
      socket.close();
    };
  }

  // A link that dropped (or never opened): raise exactly one toast on the online→retry transition (and one on
  // a first-connect failure), mark the backend reconnecting so the panes show it, and schedule a retry.
  private onDrop(): void {
    if (this.phase === "online") {
      notify("error", `Lost connection to ${this.label}. Reconnecting…`, this.connectionToastKey);
    } else if (this.phase === "connecting") {
      notify("warn", `Can't reach ${this.label}. Retrying…`, this.connectionToastKey);
    }
    this.phase = "reconnecting";
    setBackendPhase(this.backendId, "reconnecting");
    this.scheduleReconnect();
  }

  private scheduleReconnect(): void {
    const delay = this.reconnectDelayMs;
    this.reconnectDelayMs = Math.min(this.reconnectDelayMs * 2, 10_000);
    this.reconnectTimer = setTimeout(() => this.connect(), delay);
  }
}

// Resolve the remote bridge URL: a `?weavie-bridge=` query override wins, else `window.__WEAVIE_BRIDGE_WS__`.
// "auto" derives a same-origin `ws(s)://<host>/weavie-bridge` (the serve host also serves the page).
function resolveBridgeWsUrl(): string | null {
  const override = new URLSearchParams(window.location.search).get("weavie-bridge");
  const configured = override ?? window.__WEAVIE_BRIDGE_WS__ ?? "";
  if (configured === "") {
    return null;
  }
  if (configured === "auto") {
    const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
    // A remote runner serves the page at `…/?token=<t>` and gates the bridge on that token; carry it onto
    // the derived same-origin bridge URL. Absent (plain local headless), the bridge is ungated.
    const token = new URLSearchParams(window.location.search).get("token");
    const query = token === null ? "" : `?token=${encodeURIComponent(token)}`;
    return `${scheme}//${window.location.host}/weavie-bridge${query}`;
  }
  return configured;
}

// A connected backend: a transport plus its display identity. "local" is the default backend; remotes carry
// the registered agent's name (shown on the rail + the New Session location picker).
export interface BackendInfo {
  id: string;
  name: string;
  isLocal: boolean;
}

interface Backend {
  info: BackendInfo;
  transport: BridgeTransport;
}

const backends = new Map<string, Backend>();
const [backendList, setBackendList] = createSignal<BackendInfo[]>([]);

function publishBackends(): void {
  setBackendList([...backends.values()].map((b) => b.info));
}

// True when the page is served to a real browser over the WebSocket bridge (headless `serve` / a remote
// runner) rather than a native WebView shell. The OS clipboard then lives in the browser, not the host
// process, so terminal copy/paste must use navigator.clipboard instead of the (no-op) host clipboard.
let browserHostedShell = false;

/** Whether this is a browser-served shell (vs a native WebView) — see {@link browserHostedShell}. */
export function isBrowserHostedShell(): boolean {
  return browserHostedShell;
}

// The default/local backend: a native shell's in-process channel wins, else the same-origin headless
// WebSocket. With neither (plain browser on the dev server), there's no local backend and outbound is a no-op.
(() => {
  // Native delivers via window.__weavieReceive; tag it as the local backend.
  window.__weavieReceive = (raw: string): void => deliverFromHost(raw, LOCAL_BACKEND_ID);
  let transport: BridgeTransport | null = null;
  if (window.webkit?.messageHandlers?.weavie !== undefined) {
    transport = nativeTransport;
  } else {
    const wsUrl = resolveBridgeWsUrl();
    if (wsUrl !== null) {
      transport = new WebSocketTransport(LOCAL_BACKEND_ID, wsUrl, "the Weavie host");
      browserHostedShell = true;
    }
  }
  if (transport !== null) {
    backends.set(LOCAL_BACKEND_ID, {
      info: { id: LOCAL_BACKEND_ID, name: "default", isLocal: true },
      transport,
    });
    publishBackends();
  }
})();

// Connect an additional (remote) backend so its sessions appear on the rail. Its page-painting traffic stays
// suppressed until made active (see deliverFromHost). Idempotent per id.
export function connectBackend(id: string, name: string, wsUrl: string): void {
  if (backends.has(id)) {
    return;
  }
  // `ready` is the hello, re-sent on every (re)connect so the session-list comes back after a drop.
  const transport = new WebSocketTransport(id, wsUrl, name, READY_HELLO);
  backends.set(id, { info: { id, name, isLocal: false }, transport });
  publishBackends();
}

// Drop a remote backend: close its bridge (no reconnect) and remove it, so its chips leave the rail. The
// local backend is never removed; if the page was bound to this one, fall back to local. Idempotent.
export function disconnectBackend(id: string): void {
  if (id === LOCAL_BACKEND_ID) {
    return;
  }
  const backend = backends.get(id);
  if (backend === undefined) {
    return;
  }
  backend.transport.dispose();
  backends.delete(id);
  failPendingForBackend(id, "The backend was disconnected.");
  publishBackends();
  if (activeBackend() === id) {
    setActiveBackend(LOCAL_BACKEND_ID);
  }
}

/** The connected backends (local + remotes), for the location picker and rail labels. */
export const connectedBackends = backendList;

/** The id of the backend currently driving the page. */
export const activeBackendId = activeBackend;

/** Bind the page to a backend; its next session-scoped pushes (term-reset/editor) re-attach the panes. */
export function setActiveBackendId(id: string): void {
  setActiveBackend(id);
}

/** The display name of a backend id, or the id itself if unknown. */
export function backendName(id: string): string {
  return backends.get(id)?.info.name ?? id;
}

// Terminal keystrokes are live input, not queueable work: while a backend's link is down they are dropped
// (the offline overlay is the user-facing signal), never buffered — replaying stale keystrokes (with their
// Enters) into the PTY on reconnect would execute commands nobody is watching. Mirrors the LSP writer's
// reject-while-offline.
function dropWhileOffline(backendId: string, message: HostBoundMessage): boolean {
  return message.type === "term-input" && (phases().get(backendId) ?? "online") !== "online";
}

/** Send to the active backend (the page's current backend). */
export function postToHost(message: HostBoundMessage): void {
  postToBackend(activeBackend(), message);
}

/** Send to a specific backend regardless of which is active (e.g. New Session at a chosen location). */
export function postToBackend(backendId: string, message: HostBoundMessage): void {
  if (dropWhileOffline(backendId, message)) {
    return;
  }
  backends.get(backendId)?.transport.send(JSON.stringify(message));
}

/**
 * Send to the local backend — the machine the user is at. Local-machine concerns (the OS clipboard, opening
 * a browser, the native window frame) go here, never to a remote backend that happens to drive the page.
 */
export function postToLocalHost(message: HostBoundMessage): void {
  postToBackend(LOCAL_BACKEND_ID, message);
}

export function log(level: "info" | "warn" | "error", message: string): void {
  postToHost({ type: "log", level, message });
}

// The connect dialog hands the host the pasted access token to validate + save; resolves with the outcome so the
// dialog can close on success or show the rejection inline. No timeout: the host replies in both branches.
const pendingTokenRequests = new Map<string, (result: { ok: boolean; error: string }) => void>();
let tokenSeq = 0;
onHostMessage((message) => {
  if (message.type === "source-token-result") {
    pendingTokenRequests.get(message.id)?.({ ok: message.ok, error: message.error });
  }
});

// Hand an opened URL to the host's resolver: a source-claimed URL (a Notion link) is fetched + rendered natively
// (source-doc); any other URL comes back as open-web for a web tab. The match lives host-side (ISource.Match), so
// the web never re-implements a source's predicate.
export function openTarget(url: string): void {
  postToHost({ type: "open-target", url });
}

/** Saves one block edit to a source document (see the source-save-edit message). */
export function saveSourceEdit(target: string, oldStr: string, newStr: string): void {
  postToHost({ type: "source-save-edit", target, oldStr, newStr });
}

export function submitSourceToken(
  sourceId: string,
  token: string,
): Promise<{ ok: boolean; error: string }> {
  const id = `st${++tokenSeq}`;
  return new Promise((resolve) => {
    pendingTokenRequests.set(id, (result) => {
      pendingTokenRequests.delete(id);
      resolve(result);
    });
    postToHost({ type: "set-source-token", id, sourceId, token });
  });
}

export function onHostMessage(handler: WebMessageHandler): () => void {
  listeners.add(handler);
  return () => {
    listeners.delete(handler);
  };
}

/** Subscribe to session-list / session-status from EVERY backend (tagged with its id), for the rail. */
export function onSessionMessage(handler: SessionMessageHandler): () => void {
  sessionListeners.add(handler);
  return () => {
    sessionListeners.delete(handler);
  };
}

// New Session branch typeahead: ask a chosen backend for its checkout-able branches. branches-result routes
// cross-backend, so correlate replies by id and resolve empty on timeout rather than hanging.
const BRANCHES_TIMEOUT_MS = 10_000;
let branchSeq = 0;
const pendingBranchRequests = new Map<string, (branches: string[]) => void>();
onSessionMessage((message) => {
  if (message.type === "branches-result") {
    pendingBranchRequests.get(message.id)?.(message.branches);
  }
});

/** Ask `backendId` for the local branches available to check out as a new session (empty on timeout). */
export function requestBranches(backendId: string): Promise<string[]> {
  const id = `br${++branchSeq}`;
  return new Promise<string[]>((resolve) => {
    const timer = setTimeout(() => {
      pendingBranchRequests.delete(id);
      resolve([]);
    }, BRANCHES_TIMEOUT_MS);
    pendingBranchRequests.set(id, (branches) => {
      clearTimeout(timer);
      pendingBranchRequests.delete(id);
      resolve(branches);
    });
    postToBackend(backendId, { type: "list-branches", id });
  });
}

/** One open pull request, as the Open-PR picker renders it. Mirrors the host's prs-result entries. */
export interface PullRequestInfo {
  number: number;
  title: string;
  author: string;
  // The head branch — present in the default list (for display); empty for search results (refs resolve on open).
  headRef: string;
  url: string;
  draft: boolean;
}

/** One PR review comment anchored to a diff line, as the inline-diff renders it. Mirrors the host's review-comments entries. */
export interface ReviewCommentInfo {
  id: number;
  line: number;
  side: "left" | "right";
  author: string;
  body: string;
  createdAt: string;
  inReplyTo: number;
}

// Open-PR picker: ask a chosen backend for its repo's open PRs. prs-result routes cross-backend, so correlate
// replies by id and resolve empty on timeout rather than hanging (the branches-typeahead pattern).
let prSeq = 0;
const pendingPrRequests = new Map<string, (prs: PullRequestInfo[]) => void>();
const pendingResolveRequests = new Map<string, (pr: PullRequestInfo | null) => void>();
onSessionMessage((message) => {
  if (message.type === "prs-result") {
    pendingPrRequests.get(message.id)?.(message.prs);
  } else if (message.type === "pr-resolved") {
    pendingResolveRequests.get(message.id)?.(message.pr);
  }
});

/** Resolve a typed #N / pasted URL to its PR for preview (null when it doesn't exist; null on timeout). */
export function resolvePullRequest(
  backendId: string,
  target: { number: number; owner: string; repo: string },
): Promise<PullRequestInfo | null> {
  const id = `rs${++prSeq}`;
  return new Promise<PullRequestInfo | null>((resolve) => {
    const timer = setTimeout(() => {
      pendingResolveRequests.delete(id);
      resolve(null);
    }, BRANCHES_TIMEOUT_MS);
    pendingResolveRequests.set(id, (pr) => {
      clearTimeout(timer);
      pendingResolveRequests.delete(id);
      resolve(pr);
    });
    postToBackend(backendId, {
      type: "resolve-pr",
      id,
      number: target.number,
      owner: target.owner,
      repo: target.repo,
    });
  });
}

/**
 * Ask `backendId` for its repo's pull requests: an empty `query` returns the recent-open default list; a
 * non-empty query runs forge-side search (so the picker scales past the default). Empty on timeout.
 */
export function requestPullRequests(backendId: string, query: string): Promise<PullRequestInfo[]> {
  const id = `pr${++prSeq}`;
  return new Promise<PullRequestInfo[]>((resolve) => {
    const timer = setTimeout(() => {
      pendingPrRequests.delete(id);
      resolve([]);
    }, BRANCHES_TIMEOUT_MS);
    pendingPrRequests.set(id, (prs) => {
      clearTimeout(timer);
      pendingPrRequests.delete(id);
      resolve(prs);
    });
    postToBackend(backendId, { type: "list-prs", id, query });
  });
}

// Reads a host-injected window.__WEAVIE_*__ config global. In the shipped app these are always injected, so
// an absent value means the host failed to wire it — throw loudly rather than mount with drifting defaults.
// Plain-browser dev uses the fallback. `name` is the global's name, for the error message.
export function hostInjected<T>(name: string, value: T | undefined, devFallback: T): T {
  if (value !== undefined) {
    return value;
  }
  if (import.meta.env.DEV) {
    return devFallback;
  }
  throw new Error(
    `${name} was not injected by the host before navigation; the host must set it before the web app loads.`,
  );
}

// window.__weavieReceive is wired to the local backend in the backend-setup IIFE above (native shells call
// it directly; WebSocket transports feed deliverFromHost themselves).
