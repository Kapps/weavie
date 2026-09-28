// Derives Weavie's chrome semantic CSS vars (--bg/--bar/--border/--fg/--accent/--dim) from the resolved
// palette, mapping each to the closest VS Code workbench color id (spec §5) so all chrome tracks the active
// theme. Higher-level names complementing apply.ts, which publishes the raw --weavie-<key> vars.

// Each chrome variable and the VS Code color ids it resolves from, first match wins.
const CHROME_COLORS: Readonly<Record<string, readonly string[]>> = {
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
  // the diffEditor ids. diff.css consumes these (with fallbacks for themes that omit a key).
  "--diff-added": ["editorGutter.addedBackground"],
  "--diff-removed": ["editorGutter.deletedBackground"],
  "--diff-added-line": ["diffEditor.insertedLineBackground"],
  "--diff-added-text": ["diffEditor.insertedTextBackground"],
  "--diff-removed-line": ["diffEditor.removedLineBackground"],
  "--diff-removed-text": ["diffEditor.removedTextBackground"],
};

/** The chrome's semantic CSS variables resolved from a palette; variables the palette can't supply are omitted. */
export function chromeVars(colors: Readonly<Record<string, string>>): Record<string, string> {
  const vars: Record<string, string> = {};
  for (const [name, keys] of Object.entries(CHROME_COLORS)) {
    const value = keys
      .map((key) => colors[key])
      .find((color) => color !== undefined && color !== "");
    if (value !== undefined) {
      vars[name] = value;
    }
  }
  return vars;
}

/** Sets the chrome's semantic CSS variables on :root from a resolved palette. */
export function deriveChromeVars(colors: Readonly<Record<string, string>>): void {
  const root = document.documentElement;
  const vars = chromeVars(colors);
  for (const name of Object.keys(CHROME_COLORS)) {
    const value = vars[name];
    if (value !== undefined) {
      root.style.setProperty(name, value);
    } else {
      root.style.removeProperty(name);
    }
  }
}
