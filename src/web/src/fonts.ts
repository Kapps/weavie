// Typography for the editor + terminal surfaces, the interface family + size, and the prose family: the `fonts`
// device setting. Consumers read currentFonts() at creation and subscribe via onFontsChanged() for live updates.

import type { FontSpec } from "./bridge";
import { deviceSetting } from "./device-settings";

export type { FontSpec };

/** Resolved fonts: editor + terminal specs, the interface family and size, and the prose (agent replies, markdown) family. */
export interface FontConfig {
  editor: FontSpec;
  terminal: FontSpec;
  interface: string;
  /** Interface text size in px; the chrome tokens derive rows, controls and icons from it. */
  interfaceSize: number;
  prose: string;
}

declare global {
  interface Window {
    /** Resolved fonts injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_FONTS__?: FontConfig;
  }
}

// Plain-browser dev fallback (no host injection); in the shipped app a missing value throws (see
// hostInjected). Mirrors the host's defaults: a monospace stack at size 16, weight normal, and Sora at 13 for chrome/prose.
const DEFAULT_SPEC: FontSpec = {
  family: 'ui-monospace, "Cascadia Code", "SF Mono", Menlo, Consolas, "Courier New", monospace',
  size: 16,
  weight: "normal",
};

const DEFAULT_UI_FAMILY = '"Sora", system-ui, sans-serif';
const DEFAULT_CONFIG: FontConfig = {
  editor: DEFAULT_SPEC,
  terminal: DEFAULT_SPEC,
  interface: DEFAULT_UI_FAMILY,
  interfaceSize: 13,
  prose: DEFAULT_UI_FAMILY,
};

/** One line of interface text at `size`: a third taller, rounded up to an even pixel so an icon two less centres in it. */
function interfaceLineHeight(size: number): number {
  return 2 * Math.ceil((size * 1.33) / 2);
}

/** The current interface line in px, for geometry that script and stylesheet must agree on. */
export const interfaceLine = (): number => interfaceLineHeight(currentFonts().interfaceSize);

const fonts = deviceSetting("fonts", "__WEAVIE_FONTS__", window.__WEAVIE_FONTS__, DEFAULT_CONFIG);

/** The fonts to use right now — read this when creating the editor or terminal. */
export const currentFonts = fonts.current;

/** Subscribe to live font changes; returns an unsubscribe function. */
export const onFontsChanged = fonts.onChanged;

// Publish CSS font vars so DOM-rendered companions can track Monaco/xterm typography.
function publishFontVars(config: FontConfig): void {
  document.documentElement.style.setProperty("--editor-font-family", config.editor.family);
  document.documentElement.style.setProperty("--editor-font-size", `${config.editor.size}px`);
  document.documentElement.style.setProperty("--editor-font-weight", config.editor.weight);
  document.documentElement.style.setProperty("--terminal-font-family", config.terminal.family);
  document.documentElement.style.setProperty("--terminal-font-size", `${config.terminal.size}px`);
  document.documentElement.style.setProperty("--terminal-font-weight", config.terminal.weight);
  document.documentElement.style.setProperty("--interface-font-family", config.interface);
  document.documentElement.style.setProperty("--interface-font-size", `${config.interfaceSize}px`);
  document.documentElement.style.setProperty(
    "--interface-font-line",
    `${interfaceLineHeight(config.interfaceSize)}px`,
  );
  document.documentElement.style.setProperty("--prose-font-family", config.prose);
}
publishFontVars(currentFonts());
onFontsChanged(publishFontVars);
