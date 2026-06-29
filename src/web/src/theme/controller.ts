// Single source of truth for the active appearance (mode + theme per polarity + override ops), driving all
// three render surfaces (spec §6) — Monaco, xterm, chrome — off one resolved palette. Holds both polarities
// so a `system`-mode OS switch re-themes instantly with no host round-trip. Monaco-free (kept off the
// editor chunk) so it lives on the first-paint path; the editor chunk bridges in via onMonacoThemeChanged.

import { hostInjected, onHostMessage } from "../bridge";
import type { ThemeMode, ThemeSlot } from "../bridge";
import { applyColorsToCssVars } from "./apply";
import { WEAVIE_DARK, WEAVIE_DARK_ID } from "./builtin/weavie-dark";
import { WEAVIE_LIGHT, WEAVIE_LIGHT_ID } from "./builtin/weavie-light";
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
 * Host-injected initial appearance: the active mode and the theme for each polarity. Each slot carries the
 * theme id, its override ops, and — for installed (non-built-in) themes — the converted VS Code theme JSON.
 * Built-ins carry only the id; their JSON resolves here from the bundled registry.
 */
interface InjectedTheme {
  mode: ThemeMode;
  light: ThemeSlot;
  dark: ThemeSlot;
}

declare global {
  interface Window {
    /** The active appearance injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_THEME__?: InjectedTheme;
  }
}

/** Bundled built-in themes, keyed by id. The host injects only the id for these (the JSON lives here). */
const BUILTIN_THEMES: Readonly<Record<string, VsCodeColorTheme>> = {
  [WEAVIE_DARK_ID]: WEAVIE_DARK,
  [WEAVIE_LIGHT_ID]: WEAVIE_LIGHT,
};

/** One polarity's fully-resolved theme: its id, base theme, override ops, and the resolved palette. */
interface Slot {
  id: string;
  base: VsCodeColorTheme;
  ops: OverrideOp[];
  resolved: ResolvedTheme;
}

interface ThemeState {
  mode: ThemeMode;
  light: Slot;
  dark: Slot;
}

/** True when the OS currently prefers a dark color scheme (defaults to dark where matchMedia is absent). */
function prefersDark(): boolean {
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? true;
}

/** The active polarity for a mode: `system` defers to the OS; `light`/`dark` are forced. */
function isDark(mode: ThemeMode): boolean {
  return mode === "system" ? prefersDark() : mode === "dark";
}

function resolveSlot(slot: ThemeSlot, fallback: VsCodeColorTheme): Slot {
  const base = slot.theme ?? BUILTIN_THEMES[slot.id] ?? fallback;
  const ops = slot.ops ?? [];
  return { id: slot.id, base, ops, resolved: resolveTheme(base, ops) };
}

function computeState(injected: InjectedTheme): ThemeState {
  return {
    mode: injected.mode,
    light: resolveSlot(injected.light, WEAVIE_LIGHT),
    dark: resolveSlot(injected.dark, WEAVIE_DARK),
  };
}

/** The slot the active mode + OS preference currently selects. */
function activeSlot(s: ThemeState): Slot {
  return isDark(s.mode) ? s.dark : s.light;
}

// Seeded synchronously at module load so currentXtermTheme()/currentMonacoTheme() are valid before any
// terminal or the editor is created (which happens before any host push).
let version = 1;
let state: ThemeState = (() => {
  // Dev fallback is the built-in Weavie Light/Dark pair (ids only). The shipped host always injects
  // __WEAVIE_THEME__; a missing value throws (see hostInjected).
  const injected = hostInjected<InjectedTheme>("__WEAVIE_THEME__", window.__WEAVIE_THEME__, {
    mode: "system",
    light: { id: WEAVIE_LIGHT_ID },
    dark: { id: WEAVIE_DARK_ID },
  });
  return computeState(injected);
})();

const xtermSubscribers = new Set<(theme: XtermTheme) => void>();
const monacoSubscribers = new Set<(update: MonacoThemeUpdate) => void>();
const previewSubscribers = new Set<() => void>();

function monacoUpdate(): MonacoThemeUpdate {
  // Id bumped per change because a registered-extension theme can't be mutated in place; a fresh id forces
  // a clean re-register + setTheme.
  const slot = activeSlot(state);
  return {
    // Monaco settingsId must be a clean token ('#', '/', '.', spaces break lookup) and unique per change.
    id: `weavie-theme-${slot.id.replace(/[^a-zA-Z0-9]+/g, "-")}-${version}`,
    theme: { ...slot.base, ...slot.resolved },
  };
}

/** The xterm ITheme for the active theme — read this when creating a terminal. */
export function currentXtermTheme(): XtermTheme {
  return paletteToXtermTheme(activeSlot(state).resolved.colors);
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

/**
 * Subscribe to live theme changes for the Markdown preview; returns an unsubscribe function. Fired after the
 * chrome CSS vars are republished, so handlers read the fresh palette (Mermaid bakes its theme into the SVG,
 * so it must re-render on each switch). Carries no payload — the preview reads the live CSS vars directly.
 */
export function onPreviewThemeChanged(handler: () => void): () => void {
  previewSubscribers.add(handler);
  return () => {
    previewSubscribers.delete(handler);
  };
}

/** Applies the active theme to Weavie's chrome (CSS vars + color-scheme). Call once the DOM is mounted; idempotent. */
export function applyChromeTheme(): void {
  const slot = activeSlot(state);
  applyColorsToCssVars(slot.resolved.colors);
  deriveChromeVars(slot.resolved.colors);
  // Keep the UA color-scheme in step so native form controls, scrollbars, and the pre-theme flash match
  // the active polarity; mirror it onto data-theme-type so polarity-specific CSS can target it.
  const polarity = slot.base.type === "light" ? "light" : "dark";
  document.documentElement.style.colorScheme = polarity;
  document.documentElement.dataset.themeType = polarity;
}

/** Re-themes all three surfaces (chrome + xterm + Monaco) from the current active slot. */
function reapplyActive(): void {
  version += 1;
  applyChromeTheme();
  const xterm = paletteToXtermTheme(activeSlot(state).resolved.colors);
  for (const handler of xtermSubscribers) {
    handler(xterm);
  }
  const update = monacoUpdate();
  for (const handler of monacoSubscribers) {
    handler(update);
  }
  for (const handler of previewSubscribers) {
    handler();
  }
}

function setActive(injected: InjectedTheme): void {
  state = computeState(injected);
  reapplyActive();
}

// Fan host appearance pushes (mode/theme switch or override edit) out to the three surfaces. Installed
// themes arrive with their converted JSON; built-ins carry only the id, resolved against the bundled registry.
onHostMessage((message) => {
  if (message.type === "theme") {
    setActive({ mode: message.mode, light: message.light, dark: message.dark });
  }
});

// Live OS light/dark flips re-theme all surfaces in place under `system` mode (both themes are already
// resolved, so no host round-trip); a no-op under a forced mode.
window.matchMedia?.("(prefers-color-scheme: dark)").addEventListener?.("change", () => {
  if (state.mode === "system") {
    reapplyActive();
  }
});
