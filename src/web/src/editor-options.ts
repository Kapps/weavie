// Editor-behavior options (Monaco IEditorOptions) — the editor analogue of fonts.ts. The C# host owns the
// source of truth (the typed `editor.*` settings; see Core's EditorSettings) and delivers resolved values
// two ways: injected as `window.__WEAVIE_EDITOR_OPTIONS__` before navigation (so the editor mounts with the
// right options), and re-pushed as a { type: "editorOptions" } message on change. Consumers read
// currentEditorOptions() at creation and subscribe via onEditorOptionsChanged() for live updates
// (monaco-setup.ts maps these onto editor.updateOptions + the suggest-docs behavior).

import { type EditorOptionsSpec, hostInjected, onHostMessage } from "./bridge";

export type { EditorOptionsSpec };

declare global {
  interface Window {
    /** Resolved editor options injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_EDITOR_OPTIONS__?: EditorOptionsSpec;
  }
}

// Plain-browser dev fallback (no host injection); in the shipped app a missing value throws (see
// hostInjected). Mirrors the host's defaults in Core's EditorSettings, including Monaco's standard 300ms
// hover delay.
const DEFAULT_OPTIONS: EditorOptionsSpec = {
  inlayHints: "on",
  minimap: true,
  bracketPairColorization: true,
  smoothScrolling: false,
  cursorSmoothCaretAnimation: "off",
  renderWhitespace: "none",
  scrollBeyondLastLine: true,
  wordWrap: "off",
  lineNumbers: "on",
  cursorBlinking: "blink",
  renderLineHighlight: "line",
  stickyScroll: true,
  fontLigatures: false,
  indentGuides: true,
  hoverDelay: 300,
  suggestExpandDocs: true,
};

let current: EditorOptionsSpec = hostInjected(
  "__WEAVIE_EDITOR_OPTIONS__",
  window.__WEAVIE_EDITOR_OPTIONS__,
  DEFAULT_OPTIONS,
);

const subscribers = new Set<(options: EditorOptionsSpec) => void>();

/** The editor options to use right now — read this when creating the editor. */
export function currentEditorOptions(): EditorOptionsSpec {
  return current;
}

/** Subscribe to live editor-option changes; returns an unsubscribe function. */
export function onEditorOptionsChanged(handler: (options: EditorOptionsSpec) => void): () => void {
  subscribers.add(handler);
  return () => {
    subscribers.delete(handler);
  };
}

// A single permanent bridge listener (registered once at module load) fans every host push out to all
// subscribers; the editor subscribes through onEditorOptionsChanged rather than the bridge directly.
onHostMessage((message) => {
  if (message.type === "editorOptions") {
    current = message.options;
    for (const handler of subscribers) {
      handler(current);
    }
  }
});
