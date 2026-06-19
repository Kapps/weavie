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

// The left column hosts two independent PTY sessions: "claude" (the interactive Claude Code TUI)
// and "shell" (a plain login shell). Every terminal message carries which session it belongs to so
// the host can route it to the right PTY and the page can route output back to the right xterm pane.
export type TermSession = "claude" | "shell";

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

export type HostBoundMessage =
  | { type: "ready" }
  | { type: "monaco-ready" }
  | { type: "log"; level: "info" | "warn" | "error"; message: string }
  // Terminal: the xterm pane is mounted and ready to host the PTY child.
  | { type: "term-ready"; session: TermSession; cols: number; rows: number }
  | { type: "term-input"; session: TermSession; dataB64: string }
  | { type: "term-resize"; session: TermSession; cols: number; rows: number }
  // IDE-MCP: the user's Keep/Reject decision for an openDiff.
  | { type: "diff-resolved"; id: string; kept: boolean; finalContents: string }
  // Clickable file:line in the terminal -> ask the host to load + reveal the file.
  | { type: "reveal-file"; path: string; line: number }
  // The changes view asks the host for one file's session diff (baseline vs current text).
  | { type: "get-change-diff"; path: string }
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
  // Host delivers a file's contents to load + reveal in the Monaco editor.
  | { type: "open-file"; path: string; content: string; line: number }
  // Host pushes the persisted/reconciled layout (on startup, and after any layout-changed or MCP edit).
  | { type: "set-layout"; document: LayoutDocument }
  // Host pushes the persisted editor session to restore on launch/Ctrl+R. Carries NO file content — the
  // web reopens each file as a working copy resolved from disk through the host file:// provider.
  | { type: "set-editor-session"; session: EditorSession }
  // Host pushes resolved fonts when a font setting changes (ApplyMode.Live); applied to editor + terminal.
  | { type: "fonts"; editor: FontSpec; terminal: FontSpec }
  // Host pushes the active theme (a theme switch or an override edit): its id, override ops, and — for
  // installed themes — the converted VS Code theme JSON (built-ins carry only the id). Re-themes the
  // editor, terminal, and chrome live.
  | { type: "theme"; id: string; ops: OverrideOp[]; theme?: VsCodeColorTheme }
  // Host pushes the session change list (each tracked file's path + added/removed line counts).
  | {
      type: "session-changes";
      files: { path: string; name: string; added: number; removed: number }[];
    }
  // Host answers get-change-diff with one file's session baseline + current text.
  | { type: "change-diff"; path: string; name: string; baseline: string; current: string }
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

export function postToHost(message: HostBoundMessage): void {
  const handler = window.webkit?.messageHandlers?.weavie;
  if (handler === undefined) {
    return;
  }
  try {
    handler.postMessage(JSON.stringify(message));
  } catch {
    // The host channel is best-effort; never let instrumentation break the app.
  }
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

window.__weavieReceive = (raw: string): void => {
  let parsed: WebBoundMessage;
  try {
    parsed = JSON.parse(raw) as WebBoundMessage;
  } catch {
    log("error", `__weavieReceive: bad JSON: ${raw.slice(0, 200)}`);
    return;
  }
  for (const listener of listeners) {
    listener(parsed);
  }
};
