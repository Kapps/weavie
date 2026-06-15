// Typed JS <-> C# bridge over the WKWebView script-message channel.
//
//   JS  -> C#:  window.webkit.messageHandlers.weavie.postMessage(jsonString)
//   C#  -> JS:  window.__weavieReceive(jsonString)   (called from EvaluateJavaScript)
//
// Messages are JSON. When running in a plain browser (dev), the host handler is
// absent and outbound messages are no-ops — by design, never a thrown error.

import type { BenchmarkConfig, BenchmarkReport, LiveLatencyStats } from "./latency/types";

export type HostBoundMessage =
  | { type: "ready" }
  | { type: "monaco-ready" }
  | { type: "log"; level: "info" | "warn" | "error"; message: string }
  | { type: "latency-live"; stats: LiveLatencyStats }
  | { type: "benchmark-result"; report: BenchmarkReport }
  // Terminal: the xterm pane is mounted and ready to host the PTY child.
  | { type: "term-ready"; cols: number; rows: number }
  | { type: "term-input"; dataB64: string }
  | { type: "term-resize"; cols: number; rows: number }
  // IDE-MCP: the user's Keep/Reject decision for an openDiff.
  | { type: "diff-resolved"; id: string; kept: boolean; finalContents: string }
  // Clickable file:line in the terminal -> ask the host to load + reveal the file.
  | { type: "reveal-file"; path: string; line: number };

export type WebBoundMessage =
  | { type: "run-benchmark"; config?: Partial<BenchmarkConfig> }
  | { type: "set-load"; enabled: boolean }
  | { type: "term-output"; dataB64: string }
  | { type: "term-exit"; code: number }
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
  | { type: "open-file"; path: string; content: string; line: number };

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
