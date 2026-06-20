// Typed JS <-> C# bridge over the WKWebView script-message channel.
//
//   JS  -> C#:  window.webkit.messageHandlers.weavie.postMessage(jsonString)
//   C#  -> JS:  window.__weavieReceive(jsonString)   (called from EvaluateJavaScript)
//
// Messages are JSON. When running in a plain browser (dev), the host handler is
// absent and outbound messages are no-ops — by design, never a thrown error.

import type { CommandInfo, ResolvedKeybinding } from "./commands/types";
import type { EditorSession } from "./editor/session-types";
import type { LayoutDocument } from "./layout/types";
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

// The left column hosts two independent PTY sessions: "claude" (the interactive Claude Code TUI)
// and "shell" (a plain login shell). Every terminal message carries which session it belongs to so
// the host can route it to the right PTY and the page can route output back to the right xterm pane.
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
  // Terminal: the xterm pane is mounted and ready to host the PTY child.
  | { type: "term-ready"; session: TermSession; cols: number; rows: number }
  | { type: "term-input"; session: TermSession; dataB64: string }
  | { type: "term-resize"; session: TermSession; cols: number; rows: number }
  // Session rail → host: switch to a session (binds the page to it) or create a new (worktree) session.
  // new-session carries the branch name and the base: "head" (the active session's HEAD) or "main". Load /
  // unload / delete are commands (weavie.session.*) dispatched via invoke-command, not bespoke messages here.
  // The delete confirm flow is the exception: delete-session-request asks the host to classify the worktree and
  // reply with a session-delete-prompt; delete-session is the confirmed delete, its `force` set for a dirty
  // worktree. The host surfaces outcomes/failures as toasts.
  | { type: "switch-session"; id: string }
  | { type: "new-session"; branch?: string; base?: "head" | "main" }
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
  | { type: "term-output"; session: TermSession; dataB64: string }
  | { type: "term-exit"; session: TermSession; code: number }
  // Host tore down this session's PTY (e.g. the shell setting changed): clear the pane and
  // re-emit term-ready so the host relaunches the child with the new setting.
  | { type: "term-reset"; session: TermSession }
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
  // review walk's ← / → file axis (there is no panel) and the auto-arm on turn end. Pushed in auto-keep modes
  // only (acceptEdits/bypass); empty after a turn boundary (new turn).
  | {
      type: "turn-changes";
      files: { path: string; name: string; added: number; removed: number; line: number }[];
    }
  // One file's per-TURN diff (baseline-at-turn-start vs current), to render inline in the live editor.
  // baseline === current means "no markers" (the file was accepted or reverted this turn).
  | { type: "turn-diff"; path: string; name: string; baseline: string; current: string }
  // A turn boundary: clear all inline turn markers (the prior turn is implicitly accepted).
  | { type: "turn-reset" }
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
  // Host pushes the command catalog + resolved keybindings (on a live ~/.weavie/keybindings.json edit).
  | { type: "commands"; commands: CommandInfo[]; keybindings: ResolvedKeybinding[] }
  // Host asks the web to run a web command Claude invoked over MCP; the web replies with command-ack.
  | { type: "run-command"; id: string; args?: unknown; token: string };

type WebMessageHandler = (msg: WebBoundMessage) => void;

const listeners = new Set<WebMessageHandler>();

// Parse one inbound host->web JSON line and fan it out to the registered listeners. Shared by every
// transport (the native `window.__weavieReceive` callback and the WebSocket transport below) so the
// dispatch — and the loud-on-bad-JSON contract — is identical however the bytes arrived.
function deliverFromHost(raw: string): void {
  let parsed: WebBoundMessage;
  try {
    parsed = JSON.parse(raw) as WebBoundMessage;
  } catch {
    log("error", `bridge: bad JSON from host: ${raw.slice(0, 200)}`);
    return;
  }
  for (const listener of listeners) {
    listener(parsed);
  }
}

// One way to push bytes to the host. The bridge speaks the same HostBound/WebBound JSON regardless of
// how they travel; only the pipe differs.
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

  constructor(private readonly url: string) {
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
      const pending = this.outbox.splice(0, this.outbox.length);
      for (const message of pending) {
        socket.send(message);
      }
    };
    socket.onmessage = (event: MessageEvent): void => {
      if (typeof event.data === "string") {
        deliverFromHost(event.data);
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
    return `${scheme}//${window.location.host}/weavie-bridge`;
  }
  return configured;
}

// Pick the transport once, at module load. A native shell always wins (its in-process channel is
// lower-latency and already trusted). Otherwise, if a serve host advertised a WebSocket, use it. With
// neither — a plain browser opened against the dev server — outbound is a no-op and nothing is ever
// received, exactly as before: the bridge degrades silently, never throws.
const transport: BridgeTransport | null = (() => {
  if (window.webkit?.messageHandlers?.weavie !== undefined) {
    return nativeTransport;
  }
  const wsUrl = resolveBridgeWsUrl();
  return wsUrl === null ? null : new WebSocketTransport(wsUrl);
})();

export function postToHost(message: HostBoundMessage): void {
  if (transport === null) {
    return;
  }
  transport.send(JSON.stringify(message));
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

// The native shells push messages by calling this; the WebSocket transport feeds the same dispatcher
// directly. Kept for the native channel even when a WebSocket transport is active (harmless, unused).
window.__weavieReceive = deliverFromHost;
