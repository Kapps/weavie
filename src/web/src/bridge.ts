// Typed JS <-> C# bridge over the WKWebView script-message channel.
//
//   JS  -> C#:  window.webkit.messageHandlers.weavie.postMessage(jsonString)
//   C#  -> JS:  window.__weavieReceive(jsonString)   (called from EvaluateJavaScript)
//
// Messages are JSON. When running in a plain browser (dev), the host handler is
// absent and outbound messages are no-ops — by design, never a thrown error.

import type { BenchmarkConfig, BenchmarkReport, LiveLatencyStats } from "./latency/types";
import type { LayoutDocument } from "./layout/types";

// The left column hosts two independent PTY sessions: "claude" (the interactive Claude Code TUI)
// and "shell" (a plain login shell). Every terminal message carries which session it belongs to so
// the host can route it to the right PTY and the page can route output back to the right xterm pane.
export type TermSession = "claude" | "shell";

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
  | { type: "latency-live"; stats: LiveLatencyStats }
  | { type: "benchmark-result"; report: BenchmarkReport }
  // Terminal: the xterm pane is mounted and ready to host the PTY child.
  | { type: "term-ready"; session: TermSession; cols: number; rows: number }
  | { type: "term-input"; session: TermSession; dataB64: string }
  | { type: "term-resize"; session: TermSession; cols: number; rows: number }
  // IDE-MCP: the user's Keep/Reject decision for an openDiff.
  | { type: "diff-resolved"; id: string; kept: boolean; finalContents: string }
  // Clickable file:line in the terminal -> ask the host to load + reveal the file.
  | { type: "reveal-file"; path: string; line: number }
  // The user changed the pane layout (split ratio, active pane); host persists + reconciles it.
  | { type: "layout-changed"; document: LayoutDocument };

export type WebBoundMessage =
  | { type: "run-benchmark"; config?: Partial<BenchmarkConfig> }
  | { type: "set-load"; enabled: boolean }
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
  // Host pushes resolved fonts when a font setting changes (ApplyMode.Live); applied to editor + terminal.
  | { type: "fonts"; editor: FontSpec; terminal: FontSpec };

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
