// Editor-behavior options (Monaco IEditorOptions) — the editor analogue of fonts.ts. The host owns the
// source of truth (typed `editor.*` settings) and delivers it injected as `window.__WEAVIE_EDITOR_OPTIONS__`
// before navigation + re-pushed as `settings.editorOptions` on change. Consumers read currentEditorOptions()
// at creation and subscribe via onEditorOptionsChanged() for live updates.

import { type EditorOptionsSpec, hostInjected, registerHostFeature } from "./bridge";

export type { EditorOptionsSpec };

declare global {
  interface Window {
    /** Resolved editor options injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_EDITOR_OPTIONS__?: EditorOptionsSpec;
  }
}

// Plain-browser dev fallback (a missing value throws in the shipped app; see hostInjected). Mirrors the
// host's defaults in Core's EditorSettings, including Monaco's standard 300ms hover delay.
const DEFAULT_OPTIONS: EditorOptionsSpec = {
  inlayHints: "on",
  minimap: false,
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
  commentProse: "documentation",
  paneShortcutHints: true,
  videoAutoplay: true,
  gitBlame: "currentLine",
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

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host.feature("settings").on<EditorOptionsSpec>("editorOptions", (options) => {
    current = options;
    for (const handler of subscribers) {
      handler(current);
    }
  });
});
