// Editor-behavior options (Monaco IEditorOptions) for the Monaco editor — the editor analogue of
// fonts.ts. The C# host owns the source of truth (the typed `editor.*` settings; see Core's
// EditorSettings), resolves them, and delivers them two ways:
//   1. injected as `window.__WEAVIE_EDITOR_OPTIONS__` before navigation, so the editor mounts with the
//      right options (read synchronously at creation time);
//   2. re-pushed as a { type: "editorOptions" } bridge message whenever an editor setting changes
//      (ApplyMode.Live).
// Consumers read currentEditorOptions() at creation and subscribe via onEditorOptionsChanged() to apply
// live updates (monaco-setup.ts maps these onto editor.updateOptions + the suggest-docs behavior).

import { type EditorOptionsSpec, hostInjected, onHostMessage } from "./bridge";

export type { EditorOptionsSpec };

declare global {
  interface Window {
    /** Resolved editor options injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_EDITOR_OPTIONS__?: EditorOptionsSpec;
  }
}

// Plain-browser dev fallback (no host injection). Used only under `pnpm run dev`; in the shipped app the
// host always injects __WEAVIE_EDITOR_OPTIONS__ and a missing value throws (see hostInjected). Mirrors the
// host's defaults in Core's EditorSettings, including Monaco's standard 300ms hover delay (hoverDelay 300).
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

// A single permanent bridge listener fans every host push out to all subscribers, registered once at
// module load (the editor subscribes through onEditorOptionsChanged rather than the bridge directly).
onHostMessage((message) => {
  if (message.type === "editorOptions") {
    current = message.options;
    for (const handler of subscribers) {
      handler(current);
    }
  }
});
