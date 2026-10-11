// Typography for the editor + terminal surfaces and the interface + prose families. The host owns the
// source of truth (the `font.*`, `interface.font.*`, and `prose.font.*` settings) and delivers it injected as `window.__WEAVIE_FONTS__` before navigation + re-pushed as `settings.fonts` on
// change. Consumers read currentFonts() at creation and subscribe via onFontsChanged() for live updates.

import { type FontSpec, hostInjected, registerHostFeature } from "./bridge";

export type { FontSpec };

/** Resolved fonts: editor + terminal specs, and the interface + prose (agent replies, markdown) families. */
export interface FontConfig {
  editor: FontSpec;
  terminal: FontSpec;
  interface: string;
  prose: string;
}

declare global {
  interface Window {
    /** Resolved fonts injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_FONTS__?: FontConfig;
  }
}

// Plain-browser dev fallback (no host injection); in the shipped app a missing value throws (see
// hostInjected). Mirrors the host's defaults: a monospace stack at size 16, weight normal, and Chivo for chrome/prose.
const DEFAULT_SPEC: FontSpec = {
  family: 'ui-monospace, "Cascadia Code", "SF Mono", Menlo, Consolas, "Courier New", monospace',
  size: 16,
  weight: "normal",
};

const DEFAULT_UI_FAMILY = '"Chivo", system-ui, sans-serif';
const DEFAULT_CONFIG: FontConfig = {
  editor: DEFAULT_SPEC,
  terminal: DEFAULT_SPEC,
  interface: DEFAULT_UI_FAMILY,
  prose: DEFAULT_UI_FAMILY,
};

let current: FontConfig = hostInjected("__WEAVIE_FONTS__", window.__WEAVIE_FONTS__, DEFAULT_CONFIG);

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

// Publish CSS font vars so DOM-rendered companions can track Monaco/xterm typography.
function publishFontVars(config: FontConfig): void {
  document.documentElement.style.setProperty("--editor-font-family", config.editor.family);
  document.documentElement.style.setProperty("--editor-font-size", `${config.editor.size}px`);
  document.documentElement.style.setProperty("--editor-font-weight", config.editor.weight);
  document.documentElement.style.setProperty("--terminal-font-family", config.terminal.family);
  document.documentElement.style.setProperty("--terminal-font-size", `${config.terminal.size}px`);
  document.documentElement.style.setProperty("--terminal-font-weight", config.terminal.weight);
  document.documentElement.style.setProperty("--interface-font-family", config.interface);
  document.documentElement.style.setProperty("--prose-font-family", config.prose);
}
publishFontVars(current);
subscribers.add(publishFontVars);

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host.feature("settings").on<FontConfig>("fonts", (fonts) => {
    current = fonts;
    for (const handler of subscribers) {
      handler(current);
    }
  });
});
