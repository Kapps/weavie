// Derives Weavie's chrome semantic CSS vars (--bg/--bar/--border/--fg/--accent/--dim) from the resolved
// palette, mapping each to the closest VS Code workbench color id (spec §5) so all chrome tracks the active
// theme. Higher-level names complementing apply.ts, which publishes the raw --weavie-<key> vars.

import { cssVarName } from "./apply";
import { WEAVIE_DARK } from "./builtin/weavie-dark";
import { WEAVIE_LIGHT } from "./builtin/weavie-light";

/** A palette's polarity, which picks the built-in colors behind any key it omits. */
export type Polarity = "light" | "dark";

const POLARITY_DEFAULTS: Readonly<Record<Polarity, Readonly<Record<string, string>>>> = {
  light: WEAVIE_LIGHT.colors,
  dark: WEAVIE_DARK.colors,
};

// Each chrome variable and the VS Code color ids it resolves from, first match wins.
export const CHROME_COLORS: Readonly<Record<string, readonly string[]>> = {
  "--bg": ["editor.background"],
  // The "bar" surface (title bar, pane heads, menus, toolbars, popovers): an elevated panel distinct
  // from the editor background.
  "--bar": ["editorWidget.background", "dropdown.background", "sideBar.background"],
  "--border": ["panel.border", "editorGroup.border", "widget.border"],
  "--fg": ["editor.foreground", "foreground"],
  "--accent": ["focusBorder", "button.background"],
  "--button-bg": ["button.background"],
  "--button-fg": ["button.foreground"],
  "--button-hover-bg": ["button.hoverBackground", "button.background"],
  "--list-active-bg": ["list.activeSelectionBackground", "button.background"],
  "--list-active-fg": ["list.activeSelectionForeground", "button.foreground"],
  "--list-hover-bg": ["list.hoverBackground", "editorWidget.background"],
  "--list-hover-fg": ["list.hoverForeground", "editor.foreground", "foreground"],
  "--dim": ["descriptionForeground", "editorLineNumber.foreground"],
  // Session-status accents for the pane/rail indicator, mapped to the ANSI/error palette so they
  // re-theme live: --ok (idle/done), --warn (needs input), --bad (error), --busy (working/starting).
  "--ok": ["terminal.ansiGreen", "charts.green", "gitDecoration.addedResourceForeground"],
  "--warn": ["terminal.ansiYellow", "charts.yellow", "editorWarning.foreground"],
  "--bad": ["errorForeground", "terminal.ansiRed", "editorError.foreground"],
  "--busy": ["terminal.ansiBlue", "charts.blue", "focusBorder"],
  // Diff surfaces (inline change review) mapped to the standard VS Code diff color ids so they track the
  // active theme instead of hardcoding green/red: solid markers from the gutter ids, line/char washes from
  // the diffEditor ids. diff.css consumes these.
  "--diff-added": ["editorGutter.addedBackground"],
  "--diff-removed": ["editorGutter.deletedBackground"],
  "--diff-added-line": ["diffEditor.insertedLineBackground"],
  "--diff-added-text": ["diffEditor.insertedTextBackground"],
  "--diff-removed-line": ["diffEditor.removedLineBackground"],
  "--diff-removed-text": ["diffEditor.removedTextBackground"],
};

/** The chrome's semantic CSS variables from a palette; a variable it can't supply takes its polarity's built-in color. */
export function chromeVars(
  colors: Readonly<Record<string, string>>,
  polarity: Polarity,
): Record<string, string> {
  const vars: Record<string, string> = {};
  for (const [name, keys] of Object.entries(CHROME_COLORS)) {
    const value = [colors, POLARITY_DEFAULTS[polarity]]
      .flatMap((palette) => keys.map((key) => palette[key]))
      .find((color) => color !== undefined && color !== "");
    if (value !== undefined) {
      vars[name] = value;
    }
  }
  return vars;
}

/** Every CSS variable a palette publishes: the raw `--weavie-<key>` ids plus the chrome's semantic vars. */
export function paletteVars(
  colors: Readonly<Record<string, string>>,
  polarity: Polarity,
): Record<string, string> {
  const raw = Object.entries(colors).map(([key, value]) => [cssVarName(key), value]);
  return { ...Object.fromEntries(raw), ...chromeVars(colors, polarity) };
}

/** Sets the chrome's semantic CSS variables on :root from a resolved palette. */
export function deriveChromeVars(
  colors: Readonly<Record<string, string>>,
  polarity: Polarity,
): void {
  for (const [name, value] of Object.entries(chromeVars(colors, polarity))) {
    document.documentElement.style.setProperty(name, value);
  }
}
