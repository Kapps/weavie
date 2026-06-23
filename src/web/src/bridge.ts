// Typed JS <-> C# bridge over the WKWebView script-message channel (JS→C# via postMessage, C#→JS via
// window.__weavieReceive). JSON messages; in a plain browser (dev) the host handler is absent and outbound
// is a no-op by design, never a thrown error.

import { createSignal } from "solid-js";
import type { CommandInfo, ResolvedKeybinding } from "./commands/types";
import type { EditorSession } from "./editor/session-types";
import type { LayoutDocument } from "./layout/types";
import type { WeavieLspConfig } from "./lsp/lsp-client";
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
export type SessionStatusName = "starting" | "working" | "needsInput" | "idle" | "error";

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
  status: SessionStatusName;
  hue: number;
  monogram: string;
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
}

/**
 * Which comments the editor renders as prose: `none`; `documentation` (only doc comments `///`/`//!`/`/** *​/`);
 * `multiline` (doc comments plus any spanning ≥2 lines); `all` (every full-line comment).
 */
export type CommentProseMode = "none" | "documentation" | "multiline" | "all";

export type HostBoundMessage =
  | { type: "ready" }
  | { type: "monaco-ready" }
  | { type: "log"; level: "info" | "warn" | "error"; message: string }
  // The xterm pane is mounted and ready to host the PTY child. `slot` is the workspace session (rail id)
  // this pane belongs to; `session` is the pane within it.
  | { type: "term-ready"; slot: string; session: TermSession; cols: number; rows: number }
  | { type: "term-input"; slot: string; session: TermSession; dataB64: string }
  | { type: "term-resize"; slot: string; session: TermSession; cols: number; rows: number }
  // Session rail → host: switch to a session (binds the page to it). Load/unload/delete are weavie.session.*
  // commands via invoke-command; the delete confirm flow is the exception (delete-session-request →
  // session-delete-prompt → delete-session, `force` for a dirty worktree).
  | { type: "switch-session"; id: string }
  // Create a new session. `existing` ⇒ check out the EXISTING `branch` (base ignored); else create a new
  // branch off `base` ("head" = active session's HEAD, or "main"). list-branches asks a backend for its
  // checkout-able branches, answered by a branches-result tagged with the request `id`.
  | { type: "new-session"; branch?: string; base?: "head" | "main"; existing?: boolean }
  | { type: "list-branches"; id: string }
  | { type: "delete-session-request"; id: string }
  | { type: "delete-session"; id: string; force: boolean }
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
  // A terminal hyperlink / Claude's OAuth URL -> open in the OS default browser.
  | { type: "open-url"; url: string }
  // The shell child reported its working directory (OSC 7); the host relaunches the shell there on reopen.
  | { type: "term-cwd"; slot: string; session: TermSession; cwd: string }
  // The review walk asks the host for one file's turn diff (review-baseline vs current), so opening a file in
  // the review re-renders its inline applied diff even if its per-file turn-diff push was missed.
  | { type: "get-turn-diff"; path: string }
  // Host-backed file:// provider: the editor's VSCode working copies read/write disk through the host.
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
  // Remote-agent registry (host persists, web connects). add/remove persist/forget an agent. Both target the
  // local backend, since the registry is a local-machine concept. See remote-agents.ts.
  | { type: "add-remote-agent"; name: string; url: string; token: string }
  | { type: "remove-remote-agent"; name: string }
  // Session rail UI state (host-persisted in ~/.weavie/rail-state.json; both target the local backend).
  // set-last-location remembers where the last session was created; set-promoted carries the promoted set.
  | { type: "set-last-location"; location: string }
  | { type: "set-promoted"; promoted: string[] }
  // A keybinding/palette invoked a Core command — ask the host to run it (fire-and-forget for the web).
  | { type: "invoke-command"; id: string; args?: unknown }
  // Reply to a host run-command: whether the web handler ran (Claude's runCommand of a web command).
  | { type: "command-ack"; token: string; ok: boolean; error?: string };

export type WebBoundMessage =
  | { type: "term-output"; slot: string; session: TermSession; dataB64: string }
  | { type: "term-exit"; slot: string; session: TermSession; code: number }
  // Host asks this pane to reset + re-emit term-ready. The sole caller is a deliberate child relaunch (shell
  // setting changed), so `respawn` is true for a full reset. Session switches don't reset (pure show/hide).
  | { type: "term-reset"; slot: string; session: TermSession; respawn: boolean }
  // Host pushes a session's Claude status (derived from its hook stream + process supervisor).
  | { type: "session-status"; session: TermSession; status: SessionStatusName }
  // Host pushes the full session list for the rail (id, label, active, status, deterministic identity).
  | { type: "session-list"; sessions: SessionChip[] }
  // Host asks the web to move keyboard focus into a pane (kind, e.g. "terminal:claude") — pushed after a
  // session switch so a new / selected session lands focus in Claude.
  | { type: "focus-pane"; kind: string }
  // Reply to delete-session-request: the worktree's `state` drives the confirm — "clean" plain, "untracked"
  // two-step, "modified" checkbox gate. On accept the page sends delete-session (force when dirty).
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
  // Reply to clipboard-read (terminal paste), correlated by `id`: the OS clipboard's text ("" when empty).
  | { type: "clipboard-content"; id: string; text: string }
  // Host delivers a file to load + reveal in Monaco. `preview` ⇒ reusable preview tab, else persistent.
  // `scratch` marks an untitled buffer (New File / restored). `content` is ignored — the working copy reads disk.
  | {
      type: "open-file";
      path: string;
      content: string;
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
  // Host pushes resolved editor options when an editor.* setting changes (ApplyMode.Live); applied via
  // editor.updateOptions (plus the suggest-docs custom behavior).
  | { type: "editorOptions"; options: EditorOptionsSpec }
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
  // ← / → axis. `open` is the host's race-free decision to auto-open the first file. Pushed in auto-keep
  // modes only; empty (open=false) after a turn boundary / switch.
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
  // worktree root + token). Handled by rebindLanguageServices (lsp/lsp-client.ts).
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
  // Routed cross-backend (isSessionMessage) so the New Session dialog can query a non-active backend.
  | { type: "branches-result"; id: string; branches: string[] }
  // Host pushes the command catalog + resolved keybindings (on a live ~/.weavie/keybindings.json edit).
  | { type: "commands"; commands: CommandInfo[]; keybindings: ResolvedKeybinding[] }
  // Host pushes the persisted remote-agent registry (on `ready` + any add/remove); the web reconciles its
  // connections. Honored only from the local backend, so a remote runner's push is ignored. See remote-agents.ts.
  | { type: "remote-agents"; agents: { name: string; url: string; token: string }[] }
  // Host pushes the persisted session-rail UI state (on `ready` and on any change, from this or another
  // window). Honored only from the local backend. See rail-state.ts.
  | { type: "rail-state"; lastLocation: string; promoted: string[] }
  // Host asks the web to run a web command Claude invoked over MCP; the web replies with command-ack.
  | { type: "run-command"; id: string; args?: unknown; token: string };

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
const LOCAL_BACKEND_ID = "local";

// These route cross-backend (tagged with origin) rather than being dropped by the active-backend gate — they
// feed the rail, the New Session typeahead, and local-only registry/rail state. Everything else is gated to
// the bound backend.
function isSessionMessage(type: string): boolean {
  return (
    type === "session-list" ||
    type === "session-status" ||
    type === "branches-result" ||
    type === "remote-agents" ||
    type === "rail-state"
  );
}

// Parse one inbound JSON line and route it: session messages fan out to the rail listeners tagged with
// `backendId`; all else reaches page listeners only from the active backend. Shared by every transport.
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

// Remote/web Weavie: a headless "serve" host exposes the same bridge protocol over a WebSocket. Outbound
// before the socket opens is buffered and flushed on open; a dropped socket reconnects with capped backoff.
// Inbound frames go through the shared `deliverFromHost`, indistinguishable from a native host.
class WebSocketTransport implements BridgeTransport {
  private socket: WebSocket | null = null;
  private readonly outbox: string[] = [];
  private reconnectDelayMs = 500;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  // Set once the backend is deliberately disconnected; stops the close→reconnect loop for good.
  private disposed = false;

  constructor(
    private readonly backendId: string,
    private readonly url: string,
    // Re-sent on every (re)connect, so a backend re-pushes its state after a dropped link. Remotes pass
    // `ready`; the local backend leaves it undefined (main.tsx sends its initial ready).
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

  /** Disconnect for good: stop reconnecting and close the socket (the user removed this remote backend). */
  dispose(): void {
    this.disposed = true;
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    this.socket?.close();
    this.socket = null;
  }

  private connect(): void {
    if (this.disposed) {
      return;
    }
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
      if (!this.disposed) {
        this.scheduleReconnect();
      }
    };
    socket.onerror = (): void => {
      // onerror is always followed by onclose, which drives the reconnect. Close defensively so a
      // transport blip never surfaces as an uncaught error.
      socket.close();
    };
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

// Connect an additional (remote) backend so its sessions appear on the rail. Its page-painting traffic stays
// suppressed until made active (see deliverFromHost). Idempotent per id.
export function connectBackend(id: string, name: string, wsUrl: string): void {
  if (backends.has(id)) {
    return;
  }
  // `ready` is the hello, re-sent on every (re)connect so the session-list comes back after a drop.
  const transport = new WebSocketTransport(id, wsUrl, JSON.stringify({ type: "ready" }));
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
