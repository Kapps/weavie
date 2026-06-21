// Typed JS <-> C# bridge over the WKWebView script-message channel.
//
//   JS  -> C#:  window.webkit.messageHandlers.weavie.postMessage(jsonString)
//   C#  -> JS:  window.__weavieReceive(jsonString)   (called from EvaluateJavaScript)
//
// Messages are JSON. When running in a plain browser (dev), the host handler is
// absent and outbound messages are no-ops — by design, never a thrown error.

import { createSignal } from "solid-js";
import type { CommandInfo, ResolvedKeybinding } from "./commands/types";
import type { EditorSession } from "./editor/session-types";
import type { LayoutDocument } from "./layout/types";
import type { WeavieLspConfig } from "./lsp/lsp-client";
import type { OverrideOp } from "./theme/overrides";
import type { VsCodeColorTheme } from "./theme/vscode-theme";

// Appearance mode: follow the OS (`system`), or force a polarity. Resolved against `prefers-color-scheme`
// in the theme controller when `system`.
export type ThemeMode = "system" | "light" | "dark";

// One polarity's theme in a pushed/injected theme payload: the selected theme id, its ordered override
// stack, and — for installed themes only — the converted VS Code theme JSON (built-ins resolve by id).
export interface ThemeSlot {
  id: string;
  ops?: OverrideOp[];
  theme?: VsCodeColorTheme;
}

// The left column hosts two PTY panes per workspace session: "claude" (the interactive Claude Code
// TUI) and "shell" (a plain login shell). Every terminal message carries its `session` (which pane)
// AND its `slot` (which workspace session — the rail id) so the host routes it to the right PTY and
// the page routes output back to the right xterm. Each loaded session keeps its own pair of xterms
// mounted (only the active one is shown), so switching sessions is pure show/hide with no replay.
export type TermSession = "claude" | "shell";

// The live state of a session's embedded Claude, derived host-side from its hook stream + process
// supervisor and shown on the pane/rail: working (turn in progress), needsInput (permission/idle
// prompt), idle (turn ended), error (crashed), starting (launching).
export type SessionStatusName = "starting" | "working" | "needsInput" | "idle" | "error";

// One session's chip on the rail, pushed by the host in a session-list message. `hue` (0-359) and
// `monogram` are derived deterministically from the branch so a session looks the same across restarts.
// `loaded` is false for a dormant worktree (surfaced so it can't leak, but with no live backend) — the
// rail renders it faded; clicking it asks the host to load it. `status` is only meaningful when loaded.
// `primary` marks the workspace's own checkout, which has no separate worktree — it can't be unloaded or
// deleted (the rail hides those actions for it). The host orders loaded chips first, dormant ones last.
export interface SessionChip {
  id: string;
  label: string;
  active: boolean;
  loaded: boolean;
  primary: boolean;
  status: SessionStatusName;
  hue: number;
  monogram: string;
}

// A frameless-window resize edge/corner the user grabbed (Windows custom chrome). The web draws the grab
// handles and names the edge; the host maps it to the matching native resize. Mirrors Core's ResizeEdge.
export type ResizeEdge =
  | "top"
  | "bottom"
  | "left"
  | "right"
  | "top-left"
  | "top-right"
  | "bottom-left"
  | "bottom-right";

// Resolved typography for one text surface (editor or terminal). The host resolves the global +
// per-surface override font settings down to concrete values, injects them as window.__WEAVIE_FONTS__
// before navigation, and re-pushes a { type: "fonts" } message whenever a font setting changes.
export interface FontSpec {
  family: string;
  size: number;
  weight: string;
}

// Resolved editor-behavior options (Monaco IEditorOptions surfaced as Weavie settings — see Core's
// EditorSettings). The host injects them as window.__WEAVIE_EDITOR_OPTIONS__ before navigation and
// re-pushes a { type: "editorOptions" } message whenever one changes. Keys are short camelCase names
// the editor maps onto Monaco's nested option shape (editor-options.ts); `suggestExpandDocs` is the
// one non-option, mapped to a custom behavior since Monaco has no setting for it.
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
}

export type HostBoundMessage =
  | { type: "ready" }
  | { type: "monaco-ready" }
  | { type: "log"; level: "info" | "warn" | "error"; message: string }
  // Terminal: the xterm pane is mounted and ready to host the PTY child. `slot` is the workspace
  // session (rail id) this pane belongs to; `session` is the pane within it.
  | { type: "term-ready"; slot: string; session: TermSession; cols: number; rows: number }
  | { type: "term-input"; slot: string; session: TermSession; dataB64: string }
  | { type: "term-resize"; slot: string; session: TermSession; cols: number; rows: number }
  // Session rail → host: switch to a session (binds the page to it) or create a new (worktree) session.
  // new-session carries the branch name and the base: "head" (the active session's HEAD) or "main". Load /
  // unload / delete are commands (weavie.session.*) dispatched via invoke-command, not bespoke messages here.
  // The delete confirm flow is the exception: delete-session-request asks the host to classify the worktree and
  // reply with a session-delete-prompt; delete-session is the confirmed delete, its `force` set for a dirty
  // worktree. The host surfaces outcomes/failures as toasts.
  | { type: "switch-session"; id: string }
  // new-session: `existing` true ⇒ check out the EXISTING branch named by `branch` (base ignored); otherwise
  // create a new branch off `base`. list-branches asks the chosen backend for the local branches available to
  // check out (every local branch minus those already in a worktree), answered by a branches-result tagged
  // with the request `id` — used by the New Session dialog's branch typeahead.
  | { type: "new-session"; branch?: string; base?: "head" | "main"; existing?: boolean }
  | { type: "list-branches"; id: string }
  | { type: "delete-session-request"; id: string }
  | { type: "delete-session"; id: string; force: boolean }
  // IDE-MCP: the user's Keep/Reject decision for an openDiff.
  | { type: "diff-resolved"; id: string; kept: boolean; finalContents: string }
  // Clickable file:line in the terminal -> ask the host to load + reveal the file. `preview` opens it as a
  // reusable preview tab (single-click / go-to-def); omitted/false opens a persistent tab.
  | { type: "reveal-file"; path: string; line: number; preview?: boolean }
  // The review walk asks the host for one file's turn diff (review-baseline vs current), so opening a file in
  // the review re-renders its inline applied diff even if its per-file turn-diff push was missed.
  | { type: "get-turn-diff"; path: string }
  // Host-backed file:// provider: the editor's VSCode working copies read/write the real disk through the
  // host (this is how the editor persists buffers now — it replaced the old debounced save-buffer message).
  // Each request carries an `id` the host echoes on the matching fs-*-result, correlating the reply.
  | { type: "fs-stat"; id: string; path: string }
  | { type: "fs-read"; id: string; path: string }
  | { type: "fs-write"; id: string; path: string; content: string }
  // Inline diff (acceptEdits mode): accept the whole turn's changes — clears the inline markers. The host
  // snapshots the per-turn baseline to current and re-pushes an (empty) turn diff.
  | { type: "accept-turn" }
  // Inline diff (acceptEdits mode): undo the whole turn's changes — the host reverts each touched file to its
  // turn baseline on disk and live-refreshes the editor.
  | { type: "undo-turn" }
  // The file browser asks the host to list a directory under the session root (root when path is "").
  | { type: "list-dir"; path: string }
  // The user changed the pane layout (split ratio, active pane); host persists + reconciles it.
  | { type: "layout-changed"; document: LayoutDocument }
  // The editor session changed (file opened, cursor moved, scrolled); debounced; host persists it. Carries
  // the open-list + active + per-file view state, NEVER file contents (the host reads those from disk).
  | { type: "editor-session-changed"; session: EditorSession }
  // New File (Ctrl+N): ask the host to create a fresh scratch buffer (an "Untitled-N" temp file in the
  // workspace scratch dir) and push it back as an open-file with `scratch: true`.
  | { type: "new-scratch" }
  // Save a scratch buffer under a real name: the host opens a native Save dialog (default filename
  // `suggestedName`, default dir = workspace root), writes `content` to the chosen path, deletes the temp
  // file, and replies with `scratch-saved`. `path` is the scratch temp path being saved.
  | { type: "save-scratch-as"; path: string; content: string; suggestedName: string }
  // Discard a scratch buffer the user closed: delete its temp file. (The web has already dropped the tab.)
  | { type: "discard-scratch"; path: string }
  // The editor's active file or selection changed -> host updates the editor store, which tells the
  // embedded Claude what the user is looking at (selection_changed). Positions are 0-based.
  | {
      type: "active-editor-changed";
      uri: string;
      languageId: string;
      text: string;
      selection: {
        start: { line: number; character: number };
        end: { line: number; character: number };
        isEmpty: boolean;
      };
    }
  // The set of open editor tabs changed (opened / closed / activated / pinned / promoted) -> host updates the
  // editor store so Claude's getOpenEditors reports the real tab set. `path` is the web's own tab key (a
  // native path); the host derives the uri/label and echoes the path back verbatim on close-tab. No content.
  | {
      type: "open-editors-changed";
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
  // A keybinding/palette invoked a Core command — ask the host to run it (fire-and-forget for the web).
  | { type: "invoke-command"; id: string; args?: unknown }
  // Reply to a host run-command: whether the web handler ran (Claude's runCommand of a web command).
  | { type: "command-ack"; token: string; ok: boolean; error?: string };

export type WebBoundMessage =
  | { type: "term-output"; slot: string; session: TermSession; dataB64: string }
  | { type: "term-exit"; slot: string; session: TermSession; code: number }
  // Host asks this pane to reset + re-emit term-ready. The only caller now is a deliberate child
  // relaunch (the shell setting changed): `respawn` is true so the pane does a full reset, since the
  // fresh child re-establishes every mode. Session switches no longer reset — each session keeps its
  // own live xterm and switching is pure show/hide (see TerminalView).
  | { type: "term-reset"; slot: string; session: TermSession; respawn: boolean }
  // Host pushes a session's Claude status (derived from its hook stream + process supervisor).
  | { type: "session-status"; session: TermSession; status: SessionStatusName }
  // Host pushes the full session list for the rail (id, label, active, status, deterministic identity).
  | { type: "session-list"; sessions: SessionChip[] }
  // Host asks the web to move keyboard focus into a pane (kind, e.g. "terminal:claude") — pushed after a
  // session switch so a new / selected session lands focus in Claude rather than nowhere.
  | { type: "focus-pane"; kind: string }
  // Reply to delete-session-request: the host classified the worktree so the page can raise the right confirm.
  // state "clean" → plain confirm; "untracked" → two-step (untracked files would be deleted); "modified" →
  // checkbox gate (tracked changes would be lost). On accept the page sends delete-session (force when dirty).
  | {
      type: "session-delete-prompt";
      id: string;
      label: string;
      state: "clean" | "untracked" | "modified";
    }
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
  // Host delivers a file's contents to load + reveal in the Monaco editor. `preview` opens it as a reusable
  // preview tab; omitted/false opens a persistent tab. `scratch` marks an untitled buffer (New File / a
  // restored scratch). (Content is ignored — the working copy reads disk.)
  | {
      type: "open-file";
      path: string;
      content: string;
      line: number;
      preview?: boolean;
      scratch?: boolean;
    }
  // Host's reply to save-scratch-as. `savedPath` is the chosen target ("" if the user cancelled the dialog).
  // `reopen` is true when the target is inside the workspace (so the editor reopens it as a normal working
  // copy); false when saved elsewhere (the host warned via a toast and the editor just drops the scratch tab).
  | { type: "scratch-saved"; scratchPath: string; savedPath: string; reopen: boolean }
  // Host (driven by Claude's close_tab MCP tool) asks the web to close the tab for this file path.
  | { type: "close-tab"; path: string }
  // Host pushes the persisted/reconciled layout (on startup, and after any layout-changed or MCP edit).
  | { type: "set-layout"; document: LayoutDocument }
  // Host pushes the persisted editor session to restore on launch/Ctrl+R. Carries NO file content — the
  // web reopens each file as a working copy resolved from disk through the host file:// provider.
  | { type: "set-editor-session"; session: EditorSession }
  // Host pushes resolved fonts when a font setting changes (ApplyMode.Live); applied to editor + terminal.
  | { type: "fonts"; editor: FontSpec; terminal: FontSpec }
  // Host pushes resolved editor options when an editor.* setting changes (ApplyMode.Live); applied via
  // editor.updateOptions (plus the suggest-docs custom behavior).
  | { type: "editorOptions"; options: EditorOptionsSpec }
  // Host pushes the appearance mode + the theme for each polarity (a mode/theme switch or an override edit).
  // Both themes are shipped so the web can resolve `system` against the live OS setting and switch
  // light↔dark instantly + flash-free. Each slot is { id, ops, theme? } — the converted VS Code theme JSON
  // is present only for installed themes (built-ins carry only the id). Re-themes editor, terminal, chrome live.
  | { type: "theme"; mode: ThemeMode; light: ThemeSlot; dark: ThemeSlot }
  // Host-backed file:// provider replies, correlated to a request `id`. fs-stat-result: existence + stat;
  // fs-read-result: content + etag, or code:"FileNotFound" (provider falls through) or a loud error;
  // fs-write-result: post-write etag, or an error. Optional fields are absent (not null) when not applicable.
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
  // The per-TURN change list (files changed this turn + each file's first-change line). Drives the inline
  // review walk's ← / → file axis (there is no panel). `open` is the host's race-free decision that the page
  // should auto-open the first file for review now (turn end, or a switch into a session with pending review).
  // Pushed in auto-keep modes only (acceptEdits/bypass); empty (and open=false) after a turn boundary / switch.
  | {
      type: "turn-changes";
      open: boolean;
      files: { path: string; name: string; added: number; removed: number; line: number }[];
    }
  // One file's per-TURN diff (baseline-at-turn-start vs current), to render inline in the live editor.
  // baseline === current means "no markers" (the file was accepted or reverted this turn).
  | { type: "turn-diff"; path: string; name: string; baseline: string; current: string }
  // A turn boundary: clear all inline turn markers (the prior turn is implicitly accepted).
  | { type: "turn-reset" }
  // A session switch: re-point the editor's language clients at the incoming session's LSP bridge (its own
  // worktree root + token). Handled by rebindLanguageServices — see lsp/lsp-client.ts.
  | { type: "lsp-config"; config: WeavieLspConfig }
  // A user-facing notification to surface as a toast (e.g. an autosave write that failed — the user must
  // see that their work didn't reach disk, never a silent drop).
  | { type: "notify"; level: "error" | "warn" | "info"; message: string }
  // Host answers list-dir with a directory's entries (directories first), each with an absolute path.
  | {
      type: "dir-listing";
      path: string;
      entries: { name: string; path: string; isDir: boolean }[];
    }
  // Host pushes the window's chrome state so the title bar updates its maximize glyph and blur dim.
  | { type: "window-state"; maximized: boolean; focused: boolean }
  // Host answers request-file-index with the workspace root + every file's absolute path (for the omnibar).
  | { type: "file-index"; root: string; files: string[] }
  // Host answers list-branches with the local branches available to check out, tagged by the request `id`.
  // Routed cross-backend (see isSessionMessage) so the New Session dialog can query a non-active backend.
  | { type: "branches-result"; id: string; branches: string[] }
  // Host pushes the command catalog + resolved keybindings (on a live ~/.weavie/keybindings.json edit).
  | { type: "commands"; commands: CommandInfo[]; keybindings: ResolvedKeybinding[] }
  // Host asks the web to run a web command Claude invoked over MCP; the web replies with command-ack.
  | { type: "run-command"; id: string; args?: unknown; token: string };

type WebMessageHandler = (msg: WebBoundMessage) => void;
type SessionMessageHandler = (msg: WebBoundMessage, backendId: string) => void;

// Listeners that render the page — they only ever see the ACTIVE backend's traffic (terminals, editor,
// layout, diffs, …), so a background backend can never paint over what's on screen.
const listeners = new Set<WebMessageHandler>();
// Listeners for the cross-backend rail: session-list / session-status from EVERY connected backend, tagged
// with which backend they came from, so the rail can show local + remote sessions side by side.
const sessionListeners = new Set<SessionMessageHandler>();

// The id of the backend whose traffic drives the page right now. "local" is the default backend (the native
// shell's in-process host, or the same-origin headless WebSocket); remotes are added via connectBackend.
const [activeBackend, setActiveBackend] = createSignal("local");
const LOCAL_BACKEND_ID = "local";

// session-list / session-status feed the cross-backend rail; branches-result answers the New Session dialog's
// typeahead, which can target a backend that isn't the active one — so it too must route cross-backend (tagged
// with its origin) rather than being dropped by the active-backend gate. Everything else belongs to whichever
// backend the page is bound to.
function isSessionMessage(type: string): boolean {
  return type === "session-list" || type === "session-status" || type === "branches-result";
}

// Parse one inbound host->web JSON line and route it. Session messages fan out to the rail listeners tagged
// with `backendId`; all other messages reach the page listeners only when they come from the active backend.
// Shared by every transport so the dispatch — and the loud-on-bad-JSON contract — is identical for all.
function deliverFromHost(raw: string, backendId: string): void {
  let parsed: WebBoundMessage;
  try {
    parsed = JSON.parse(raw) as WebBoundMessage;
  } catch {
    log("error", `bridge: bad JSON from ${backendId}: ${raw.slice(0, 200)}`);
    return;
  }
  if (isSessionMessage(parsed.type)) {
    for (const listener of sessionListeners) {
      listener(parsed, backendId);
    }
    return;
  }
  if (backendId !== activeBackend()) {
    return; // a background backend never paints the page
  }
  for (const listener of listeners) {
    listener(parsed);
  }
}

// One way to push bytes to a backend. The bridge speaks the same HostBound/WebBound JSON regardless of how
// they travel; only the pipe differs.
interface BridgeTransport {
  send(json: string): void;
}

// The native desktop shells (Win/Mac/Linux) inject `window.webkit.messageHandlers.weavie` and call
// `window.__weavieReceive` — the in-process script-message channel. Best-effort: a throwing channel
// must never break the app (this also carries diagnostic logs).
const nativeTransport: BridgeTransport = {
  send(json: string): void {
    const handler = window.webkit?.messageHandlers?.weavie;
    if (handler === undefined) {
      return;
    }
    try {
      handler.postMessage(json);
    } catch {
      // The host channel is best-effort; never let instrumentation break the app.
    }
  },
};

// Remote/web Weavie: no native shell, but a headless "serve" host exposes the same bridge protocol
// over a WebSocket. Outbound messages sent before the socket opens (notably the initial "ready") are
// buffered and flushed on open; a dropped socket reconnects with capped backoff. Inbound frames go
// through the shared `deliverFromHost`, so the page can't tell a WebSocket host from a native one.
class WebSocketTransport implements BridgeTransport {
  private socket: WebSocket | null = null;
  private readonly outbox: string[] = [];
  private reconnectDelayMs = 500;

  constructor(
    private readonly backendId: string,
    private readonly url: string,
    // Re-sent on every (re)connect, so a backend re-pushes its state after a dropped link (e.g. Tailscale
    // blip). Remotes pass `ready`; the local backend leaves it undefined (main.tsx sends its initial ready).
    private readonly hello?: string,
  ) {
    this.connect();
  }

  send(json: string): void {
    if (this.socket !== null && this.socket.readyState === WebSocket.OPEN) {
      this.socket.send(json);
      return;
    }
    this.outbox.push(json);
  }

  private connect(): void {
    let socket: WebSocket;
    try {
      socket = new WebSocket(this.url);
    } catch {
      this.scheduleReconnect();
      return;
    }
    this.socket = socket;
    socket.onopen = (): void => {
      this.reconnectDelayMs = 500;
      if (this.hello !== undefined) {
        socket.send(this.hello);
      }
      const pending = this.outbox.splice(0, this.outbox.length);
      for (const message of pending) {
        socket.send(message);
      }
    };
    socket.onmessage = (event: MessageEvent): void => {
      if (typeof event.data === "string") {
        deliverFromHost(event.data, this.backendId);
      }
    };
    socket.onclose = (): void => {
      this.socket = null;
      this.scheduleReconnect();
    };
    socket.onerror = (): void => {
      // onerror is always followed by onclose, which drives the reconnect. Close defensively and
      // swallow so a transport blip never surfaces as an uncaught error.
      socket.close();
    };
  }

  private scheduleReconnect(): void {
    const delay = this.reconnectDelayMs;
    this.reconnectDelayMs = Math.min(this.reconnectDelayMs * 2, 10_000);
    setTimeout(() => this.connect(), delay);
  }
}

// Resolve the remote bridge URL, if any: a `?weavie-bridge=` query override (handy for manual testing)
// wins, else the host-injected `window.__WEAVIE_BRIDGE_WS__`. The literal "auto" derives a same-origin
// `ws(s)://<host>/weavie-bridge` — the common case when the serve host also serves the page.
function resolveBridgeWsUrl(): string | null {
  const override = new URLSearchParams(window.location.search).get("weavie-bridge");
  const configured = override ?? window.__WEAVIE_BRIDGE_WS__ ?? "";
  if (configured === "") {
    return null;
  }
  if (configured === "auto") {
    const scheme = window.location.protocol === "https:" ? "wss:" : "ws:";
    // A remote runner serves the page at `…/?token=<t>` and gates the bridge on that token; carry it onto
    // the derived same-origin bridge URL. Absent (the plain local headless), the bridge is ungated.
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

// The default/local backend: a native shell's in-process channel always wins (lower-latency, already
// trusted); otherwise the same-origin headless WebSocket. With neither — a plain browser on the dev server —
// there is no local backend and outbound is a no-op, exactly as before: the bridge degrades silently.
(() => {
  // Native always delivers via window.__weavieReceive; tag it as the local backend.
  window.__weavieReceive = (raw: string): void => deliverFromHost(raw, LOCAL_BACKEND_ID);
  let transport: BridgeTransport | null = null;
  if (window.webkit?.messageHandlers?.weavie !== undefined) {
    transport = nativeTransport;
  } else {
    const wsUrl = resolveBridgeWsUrl();
    transport = wsUrl === null ? null : new WebSocketTransport(LOCAL_BACKEND_ID, wsUrl);
  }
  if (transport !== null) {
    backends.set(LOCAL_BACKEND_ID, {
      info: { id: LOCAL_BACKEND_ID, name: "default", isLocal: true },
      transport,
    });
    publishBackends();
  }
})();

// Connect an additional (remote) backend — a runner's worker bridge — and ask it to push its session-list so
// its sessions appear on the rail immediately. Its page-painting traffic stays suppressed until it is made
// active (see deliverFromHost). Idempotent per id.
export function connectBackend(id: string, name: string, wsUrl: string): void {
  if (backends.has(id)) {
    return;
  }
  // `ready` is the hello, re-sent on every (re)connect so its session-list comes back after a drop.
  const transport = new WebSocketTransport(id, wsUrl, JSON.stringify({ type: "ready" }));
  backends.set(id, { info: { id, name, isLocal: false }, transport });
  publishBackends();
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

/** Send to the active backend (the page's current backend). */
export function postToHost(message: HostBoundMessage): void {
  backends.get(activeBackend())?.transport.send(JSON.stringify(message));
}

/** Send to a specific backend regardless of which is active (e.g. New Session at a chosen location). */
export function postToBackend(backendId: string, message: HostBoundMessage): void {
  backends.get(backendId)?.transport.send(JSON.stringify(message));
}

export function log(level: "info" | "warn" | "error", message: string): void {
  postToHost({ type: "log", level, message });
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

// New Session branch typeahead: ask a chosen backend (local or a remote agent) for its checkout-able local
// branches. branches-result routes cross-backend (isSessionMessage), so we correlate replies by a unique id
// and resolve empty if the host never answers — the typeahead simply offers nothing rather than hanging.
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

// Reads a config value the C# host injects as a window.__WEAVIE_*__ global before navigation. In the
// shipped app the host always injects these before the web loads, so an absent value means the host
// failed to wire it — we throw loudly instead of silently mounting with dev defaults that can drift from
// Core's. In plain-browser dev (`pnpm run dev`, no host) there is legitimately no host, so the dev fallback
// is used. `name` is the global's name, for the error message.
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
// it directly; the WebSocket transports feed deliverFromHost themselves).
