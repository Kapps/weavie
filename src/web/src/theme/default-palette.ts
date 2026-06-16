// A small built-in default color palette (VS Code "Dark Modern"–ish values) for the surfaces the
// monaco-vscode-api theme stack does NOT cover on its own — the terminal (xterm) and Weavie's own
// chrome (CSS vars). Keyed by VS Code workbench color names (spec §5) so a real installed theme can
// drop in 1:1 later, and so overrides (spec §6) address the same keys. Bundled read-only so there's
// always something before any Open VSX install.

/** The default dark palette, keyed by VS Code workbench color id. */
export const DEFAULT_DARK_PALETTE: Readonly<Record<string, string>> = {
  "editor.background": "#1f1f1f",
  "editor.foreground": "#d4d4d4",
  focusBorder: "#0078d4",
  "panel.background": "#181818",
  "panel.border": "#2b2b2b",
  "sideBar.background": "#181818",
  "statusBar.background": "#181818",
  "statusBar.foreground": "#cccccc",
  "tab.activeBackground": "#1f1f1f",
  "tab.inactiveBackground": "#181818",
  "terminal.background": "#181818",
  "terminal.foreground": "#cccccc",
  "terminal.ansiBlack": "#000000",
  "terminal.ansiRed": "#cd3131",
  "terminal.ansiGreen": "#0dbc79",
  "terminal.ansiYellow": "#e5e510",
  "terminal.ansiBlue": "#2472c8",
  "terminal.ansiMagenta": "#bc3fbc",
  "terminal.ansiCyan": "#11a8cd",
  "terminal.ansiWhite": "#e5e5e5",
  "terminal.ansiBrightBlack": "#666666",
  "terminal.ansiBrightRed": "#f14c4c",
  "terminal.ansiBrightGreen": "#23d18b",
  "terminal.ansiBrightYellow": "#f5f543",
  "terminal.ansiBrightBlue": "#3b8eea",
  "terminal.ansiBrightMagenta": "#d670d6",
  "terminal.ansiBrightCyan": "#29b8db",
  "terminal.ansiBrightWhite": "#e5e5e5",
};
