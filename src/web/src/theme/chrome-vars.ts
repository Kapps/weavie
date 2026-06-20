// Derives Weavie's chrome semantic CSS vars (--bg/--bar/--border/--fg/--accent/--dim) from the active
// theme's resolved palette. The chrome (styles.css) keeps its small, stable vocabulary; this maps each
// var to the closest VS Code workbench color id (spec §5) so the title bar, panels, menus, omnibar, and
// splitters all track the active theme instead of hardcoding colors. The companion to apply.ts, which
// publishes the raw --weavie-<key> vars; these six are the chrome's own higher-level names.

/** Sets the chrome's --bg/--bar/--border/--fg/--accent/--dim vars on :root from a resolved palette. */
export function deriveChromeVars(colors: Readonly<Record<string, string>>): void {
  const root = document.documentElement;
  const pick = (...keys: string[]): string | undefined => {
    for (const key of keys) {
      const value = colors[key];
      if (value !== undefined && value.length > 0) {
        return value;
      }
    }
    return undefined;
  };
  const set = (name: string, value: string | undefined): void => {
    if (value !== undefined) {
      root.style.setProperty(name, value);
    }
  };

  set("--bg", pick("editor.background"));
  // The chrome's secondary "bar" surface — title bar, pane heads, menus, toolbars, popovers: a slightly
  // elevated panel distinct from the editor background.
  set("--bar", pick("editorWidget.background", "dropdown.background", "sideBar.background"));
  set("--border", pick("panel.border", "editorGroup.border", "widget.border"));
  set("--fg", pick("editor.foreground", "foreground"));
  set("--accent", pick("focusBorder", "button.background"));
  set("--dim", pick("descriptionForeground", "editorLineNumber.foreground"));

  // Session-status accents for the pane/rail indicator, mapped to the ANSI/error palette so they
  // re-theme live: --ok (idle/done), --warn (needs input), --bad (error), --busy (working/starting).
  set("--ok", pick("terminal.ansiGreen", "charts.green", "gitDecoration.addedResourceForeground"));
  set("--warn", pick("terminal.ansiYellow", "charts.yellow", "editorWarning.foreground"));
  set("--bad", pick("errorForeground", "terminal.ansiRed", "editorError.foreground"));
  set("--busy", pick("terminal.ansiBlue", "charts.blue", "focusBorder"));
}
