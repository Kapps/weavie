// Single source of truth for the active appearance (mode + theme per polarity + override ops), driving all
// three render surfaces (spec §6) — Monaco, xterm, chrome. An optional editor theme per polarity recolors
// Monaco and every `[data-editor-theme]` surface; unset, the editor follows the app palette. Holds both polarities
// so a `system`-mode OS switch re-themes instantly with no host round-trip. Monaco-free (kept off the
// editor chunk) so it lives on the first-paint path; the editor chunk bridges in via onMonacoThemeChanged.

import { createSignal } from "solid-js";
import type { ThemeMode, ThemeSlot } from "../bridge";
import { hostInjected, registerHostFeature } from "../bridge";
import { applyColorsToCssVars } from "./apply";
import { WEAVIE_DARK, WEAVIE_DARK_ID } from "./builtin/weavie-dark";
import { WEAVIE_LIGHT, WEAVIE_LIGHT_ID } from "./builtin/weavie-light";
import { deriveChromeVars, paletteVars } from "./chrome-vars";
import { type OverrideOp, type ResolvedTheme, resolveTheme } from "./overrides";
import type { VsCodeColorTheme } from "./vscode-theme";
import { paletteToXtermTheme, type XtermTheme } from "./xterm-theme";

/** A Monaco theme to register+apply: a unique id (bumped per change to force re-registration) + the theme. */
export interface MonacoThemeUpdate {
  id: string;
  theme: VsCodeColorTheme;
}

/**
 * Host-injected initial appearance: the active mode plus the app and optional editor theme per polarity.
 * Each slot carries the theme id, its override ops, and — for installed (non-built-in) themes — the converted VS Code theme JSON.
 * Built-ins carry only the id; their JSON resolves here from the bundled registry.
 */
interface InjectedTheme {
  mode: ThemeMode;
  light: ThemeSlot;
  dark: ThemeSlot;
  editorLight?: ThemeSlot;
  editorDark?: ThemeSlot;
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
  editorLight: Slot | undefined;
  editorDark: Slot | undefined;
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
  const light = resolveSlot(injected.light, WEAVIE_LIGHT);
  const dark = resolveSlot(injected.dark, WEAVIE_DARK);
  return {
    mode: injected.mode,
    light,
    dark,
    editorLight: injected.editorLight && resolveSlot(injected.editorLight, light.base),
    editorDark: injected.editorDark && resolveSlot(injected.editorDark, dark.base),
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

const appearanceOf = (s: ThemeState) => ({ mode: s.mode, light: s.light.id, dark: s.dark.id });
const [appearance, setAppearance] = createSignal(appearanceOf(state));

/** The saved mode and per-polarity theme ids, updated live as the host pushes changes. */
export const savedAppearance = appearance;

/** One polarity's saved palette, updated live, for previewing a theme without applying it. */
export function savedPalette(type: "light" | "dark"): Readonly<Record<string, string>> {
  appearance();
  return state[type].resolved.colors;
}

let preview: Slot | null = null;
const displayedSlot = (): Slot => preview ?? activeSlot(state);
// The picker previews the app theme, so a set editor theme stays put while the app theme is previewed.
const editorSlot = (): Slot =>
  (isDark(state.mode) ? state.editorDark : state.editorLight) ?? displayedSlot();

/** The resolved appearance mode, including the system preference. */
export const currentThemeType = (): "light" | "dark" => (isDark(state.mode) ? "dark" : "light");

/** The saved active theme, independent of a picker preview. */
export const currentThemeId = (): string => activeSlot(state).id;

/** Owns one transient preview; disposing restores the latest host appearance. */
export function beginThemePreview(): {
  show: (slot: ThemeSlot) => void;
  clear: () => void;
  dispose: () => void;
} {
  let disposed = false;
  return {
    show(slot) {
      if (disposed) return;
      const base = slot.theme ?? BUILTIN_THEMES[slot.id];
      if (base === undefined) throw new Error(`No theme data for ${slot.id}`);
      preview = resolveSlot(slot, base);
      reapplyActive();
    },
    clear() {
      if (disposed) return;
      preview = null;
      reapplyActive();
    },
    dispose() {
      disposed = true;
      preview = null;
      reapplyActive();
    },
  };
}

const xtermSubscribers = new Set<(theme: XtermTheme) => void>();
const monacoSubscribers = new Set<(update: MonacoThemeUpdate) => void>();
const previewSubscribers = new Set<() => void>();

function monacoUpdate(): MonacoThemeUpdate {
  // Id bumped per change because a registered-extension theme can't be mutated in place; a fresh id forces
  // a clean re-register + setTheme.
  const slot = editorSlot();
  return {
    // Monaco settingsId must be a clean token ('#', '/', '.', spaces break lookup) and unique per change.
    id: `weavie-theme-${slot.id.replace(/[^a-zA-Z0-9]+/g, "-")}-${version}`,
    theme: { ...slot.base, ...slot.resolved },
  };
}

/** The xterm ITheme for the active theme — read this when creating a terminal. */
export function currentXtermTheme(): XtermTheme {
  return paletteToXtermTheme(displayedSlot().resolved.colors);
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

const polarityOf = (slot: Slot): "light" | "dark" =>
  slot.base.type === "light" || slot.base.type === "hcLight" ? "light" : "dark";

let editorRule: CSSStyleDeclaration | undefined;

/** Republishes the editor palette's CSS vars on the `[data-editor-theme]` rule, shadowing :root's. */
function applyEditorTheme(): void {
  if (editorRule === undefined) {
    const element = document.createElement("style");
    element.textContent = "[data-editor-theme] {}";
    document.head.append(element);
    editorRule = (element.sheet!.cssRules[0] as CSSStyleRule).style;
  }
  const slot = editorSlot();
  const vars = paletteVars(slot.resolved.colors);
  // A var the editor palette lacks resets to `initial`, so CSS fallbacks apply instead of the app's value.
  const names = new Set([
    ...Object.keys(paletteVars(displayedSlot().resolved.colors)),
    ...Object.keys(vars),
  ]);
  editorRule.cssText = "";
  editorRule.setProperty("color-scheme", polarityOf(slot));
  for (const name of names) {
    editorRule.setProperty(name, vars[name] ?? "initial");
  }
}

/** Applies the active theme to Weavie's chrome (CSS vars + color-scheme). Call once the DOM is mounted; idempotent. */
export function applyChromeTheme(): void {
  const slot = displayedSlot();
  applyEditorTheme();
  applyColorsToCssVars(slot.resolved.colors);
  deriveChromeVars(slot.resolved.colors);
  const background = slot.resolved.colors["editor.background"];
  if (background !== undefined) {
    document
      .querySelector<HTMLMetaElement>('meta[name="theme-color"]')
      ?.setAttribute("content", background);
  }
  // Keep the UA color-scheme in step so native form controls, scrollbars, and the pre-theme flash match
  // the active polarity; mirror it onto data-theme-type so polarity-specific CSS can target it.
  const polarity = polarityOf(slot);
  document.documentElement.style.colorScheme = polarity;
  document.documentElement.dataset.themeType = polarity;
}

/** Re-themes all three surfaces (chrome + xterm + Monaco) from the current active slot. */
function reapplyActive(): void {
  version += 1;
  applyChromeTheme();
  const xterm = paletteToXtermTheme(displayedSlot().resolved.colors);
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
  setAppearance(appearanceOf(state));
  reapplyActive();
}

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host.feature("settings").on<InjectedTheme>("theme", setActive);
});

// Live OS light/dark flips re-theme all surfaces in place under `system` mode (both themes are already
// resolved, so no host round-trip); a no-op under a forced mode.
window.matchMedia?.("(prefers-color-scheme: dark)").addEventListener?.("change", () => {
  if (state.mode === "system") {
    reapplyActive();
  }
});
