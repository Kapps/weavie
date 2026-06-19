// The runtime theme controller: the single source of truth for the active theme + its override ops, and
// the thing that drives all three render surfaces (spec §6) — Monaco, xterm, and Weavie's chrome — off one
// resolved palette. Deliberately monaco-free (no editor-chunk imports) so it lives on the entry/first-paint
// path; the editor chunk bridges controller → Monaco via onMonacoThemeChanged. Mirrors fonts.ts: state is
// seeded synchronously from a host-injected global so the terminal/editor read the right theme at creation,
// and one permanent bridge listener fans live host pushes out to every surface.

import { onHostMessage } from "../bridge";
import { applyColorsToCssVars } from "./apply";
import { WEAVIE_DARK, WEAVIE_DARK_ID } from "./builtin/weavie-dark";
import { deriveChromeVars } from "./chrome-vars";
import { type OverrideOp, type ResolvedTheme, resolveTheme } from "./overrides";
import type { VsCodeColorTheme } from "./vscode-theme";
import { type XtermTheme, paletteToXtermTheme } from "./xterm-theme";

/** A Monaco theme to register+apply: a unique id (bumped per change to force re-registration) + the theme. */
export interface MonacoThemeUpdate {
  id: string;
  theme: VsCodeColorTheme;
}

/**
 * Host-injected initial theme (mirrors __WEAVIE_FONTS__): the active theme id, its override ops, and — for
 * installed (non-built-in) themes — the converted VS Code theme JSON. Built-ins carry only the id and are
 * resolved here from the bundled registry.
 */
interface InjectedTheme {
  id: string;
  ops?: OverrideOp[];
  theme?: VsCodeColorTheme;
}

declare global {
  interface Window {
    /** The active theme injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_THEME__?: InjectedTheme;
  }
}

/** Bundled built-in themes, keyed by id. The host injects only the id for these (the JSON lives here). */
const BUILTIN_THEMES: Readonly<Record<string, VsCodeColorTheme>> = {
  [WEAVIE_DARK_ID]: WEAVIE_DARK,
};

interface ThemeState {
  id: string;
  base: VsCodeColorTheme;
  ops: OverrideOp[];
  resolved: ResolvedTheme;
}

function baseFor(id: string, theme: VsCodeColorTheme | undefined): VsCodeColorTheme {
  return theme ?? BUILTIN_THEMES[id] ?? WEAVIE_DARK;
}

function computeState(id: string, base: VsCodeColorTheme, ops: OverrideOp[]): ThemeState {
  return { id, base, ops, resolved: resolveTheme(base, ops) };
}

// Seeded synchronously at module load so currentXtermTheme()/currentMonacoTheme() are valid the moment a
// terminal or the editor is created (which happens before any host push).
let version = 1;
let state: ThemeState = (() => {
  const injected = window.__WEAVIE_THEME__;
  if (injected === undefined) {
    return computeState(WEAVIE_DARK_ID, WEAVIE_DARK, []);
  }
  return computeState(injected.id, baseFor(injected.id, injected.theme), injected.ops ?? []);
})();

const xtermSubscribers = new Set<(theme: XtermTheme) => void>();
const monacoSubscribers = new Set<(update: MonacoThemeUpdate) => void>();

function monacoUpdate(): MonacoThemeUpdate {
  // Effective theme = the base theme's token tables + the resolved (override-applied) workbench colors.
  // The id is bumped per change because the theme is registered as an extension and can't be mutated in
  // place — a fresh id forces a clean re-register + setTheme.
  return {
    // A Monaco theme settingsId must be a clean token — '#', '/', '.', and spaces break theme lookup — and
    // unique per change so a re-register actually switches. Derive one from the theme id + version counter.
    id: `weavie-theme-${state.id.replace(/[^a-zA-Z0-9]+/g, "-")}-${version}`,
    theme: { ...state.base, ...state.resolved },
  };
}

/** The xterm ITheme for the active theme — read this when creating a terminal. */
export function currentXtermTheme(): XtermTheme {
  return paletteToXtermTheme(state.resolved.colors);
}

/** The Monaco theme to register+apply — read this once the editor services are initialized. */
export function currentMonacoTheme(): MonacoThemeUpdate {
  return monacoUpdate();
}

/** Subscribe to live xterm theme changes; returns an unsubscribe function. */
export function onXtermThemeChanged(handler: (theme: XtermTheme) => void): () => void {
  xtermSubscribers.add(handler);
  return () => {
    xtermSubscribers.delete(handler);
  };
}

/** Subscribe to live Monaco theme changes; returns an unsubscribe function. */
export function onMonacoThemeChanged(handler: (update: MonacoThemeUpdate) => void): () => void {
  monacoSubscribers.add(handler);
  return () => {
    monacoSubscribers.delete(handler);
  };
}

/** Applies the active theme to Weavie's chrome (CSS vars). Call once the DOM is mounted; idempotent. */
export function applyChromeTheme(): void {
  applyColorsToCssVars(state.resolved.colors);
  deriveChromeVars(state.resolved.colors);
}

function setActive(id: string, base: VsCodeColorTheme, ops: OverrideOp[]): void {
  version += 1;
  state = computeState(id, base, ops);
  applyChromeTheme();
  const xterm = paletteToXtermTheme(state.resolved.colors);
  for (const handler of xtermSubscribers) {
    handler(xterm);
  }
  const update = monacoUpdate();
  for (const handler of monacoSubscribers) {
    handler(update);
  }
}

// One permanent bridge listener fans host theme pushes (an active-theme switch or an override edit) out to
// the three surfaces — mirrors fonts.ts. Installed themes arrive with their converted JSON; built-ins carry
// only the id, resolved here against the bundled registry.
onHostMessage((message) => {
  if (message.type === "theme") {
    setActive(message.id, baseFor(message.id, message.theme), message.ops ?? []);
  }
});
