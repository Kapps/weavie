// Typography for the two text surfaces (the Monaco editor and the xterm terminal). The C# host owns
// the source of truth — global `font.*` settings with per-surface `editor.font.*` / `terminal.font.*`
// overrides — resolves them to concrete values, and delivers them two ways:
//   1. injected as `window.__WEAVIE_FONTS__` before navigation, so both surfaces mount at the right
//      font with no default-font flash (read synchronously at creation time);
//   2. re-pushed as a { type: "fonts" } bridge message whenever a font setting changes (ApplyMode.Live).
// Consumers read currentFonts() at creation and subscribe via onFontsChanged() to apply live updates.

import { type FontSpec, onHostMessage } from "./bridge";

export type { FontSpec };

/** Resolved fonts for both surfaces. */
export interface FontConfig {
  editor: FontSpec;
  terminal: FontSpec;
}

declare global {
  interface Window {
    /** Resolved editor + terminal fonts injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_FONTS__?: FontConfig;
  }
}

// Plain-browser dev fallback (no host injection). Mirrors the host's defaults: one cross-platform
// monospace stack, size 13, weight normal — both surfaces inherit the same global.
const DEFAULT_SPEC: FontSpec = {
  family: 'ui-monospace, "Cascadia Code", "SF Mono", Menlo, Consolas, "Courier New", monospace',
  size: 13,
  weight: "normal",
};

const DEFAULT_CONFIG: FontConfig = { editor: DEFAULT_SPEC, terminal: DEFAULT_SPEC };

let current: FontConfig = window.__WEAVIE_FONTS__ ?? DEFAULT_CONFIG;

const subscribers = new Set<(config: FontConfig) => void>();

/** The fonts to use right now — read this when creating the editor or terminal. */
export function currentFonts(): FontConfig {
  return current;
}

/** Subscribe to live font changes; returns an unsubscribe function. */
export function onFontsChanged(handler: (config: FontConfig) => void): () => void {
  subscribers.add(handler);
  return () => {
    subscribers.delete(handler);
  };
}

// A single, permanent bridge listener fans every host font push out to all subscribers. Registered
// once at module load; the editor/terminal subscribe through onFontsChanged rather than the bridge.
onHostMessage((message) => {
  if (message.type === "fonts") {
    current = { editor: message.editor, terminal: message.terminal };
    for (const handler of subscribers) {
      handler(current);
    }
  }
});
