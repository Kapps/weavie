// Typography for the editor + terminal surfaces. The host owns the source of truth (`font.*` settings) and
// delivers it injected as `window.__WEAVIE_FONTS__` before navigation + re-pushed as { type: "fonts" } on
// change. Consumers read currentFonts() at creation and subscribe via onFontsChanged() for live updates.

import { type FontSpec, hostInjected, onHostMessage } from "./bridge";

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

// Plain-browser dev fallback (no host injection); in the shipped app a missing value throws (see
// hostInjected). Mirrors the host's defaults: one cross-platform monospace stack, size 16, weight normal.
const DEFAULT_SPEC: FontSpec = {
  family: 'ui-monospace, "Cascadia Code", "SF Mono", Menlo, Consolas, "Courier New", monospace',
  size: 16,
  weight: "normal",
};

const DEFAULT_CONFIG: FontConfig = { editor: DEFAULT_SPEC, terminal: DEFAULT_SPEC };

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

// Publish the editor font size as a :root CSS var so CSS-styled DOM surfaces (the Markdown Preview) can scale
// to it declaratively, the way chrome/theme vars work — Monaco/xterm instead push the size into their own
// options. Set now and on every change (it's registered as a subscriber below).
function publishFontVars(config: FontConfig): void {
  document.documentElement.style.setProperty("--editor-font-size", `${config.editor.size}px`);
}
publishFontVars(current);
subscribers.add(publishFontVars);

// A single permanent bridge listener (registered once at module load) fans every host font push out to all
// subscribers; the editor/terminal subscribe through onFontsChanged rather than the bridge.
onHostMessage((message) => {
  if (message.type === "fonts") {
    current = { editor: message.editor, terminal: message.terminal };
    for (const handler of subscribers) {
      handler(current);
    }
  }
});
