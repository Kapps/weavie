// Builds an xterm.js ITheme from a resolved palette (spec §6: one of the three live-reapply surfaces).
// Keyed by the same VS Code `terminal.*` ids, so installed themes and overrides drive terminal colors for
// free. Missing keys fall back to the editor background/foreground or are omitted (xterm keeps its default).

import type { ITheme } from "@xterm/xterm";

/** The xterm theme Weavie derives from a VS Code palette (xterm's ITheme, all fields optional). */
export type XtermTheme = ITheme;

/** Maps a resolved VS Code palette to an xterm ITheme, omitting any key the palette doesn't provide. */
export function paletteToXtermTheme(colors: Readonly<Record<string, string>>): ITheme {
  // Under exactOptionalPropertyTypes an optional ITheme field can't be `undefined`, so omit missing colors.
  const theme: Record<string, string> = {};
  const put = (field: string, ...keys: string[]): void => {
    for (const key of keys) {
      const value = colors[key];
      if (value !== undefined && value.length > 0) {
        theme[field] = value;
        return;
      }
    }
  };

  put("background", "terminal.background", "editor.background");
  put("foreground", "terminal.foreground", "editor.foreground");
  put("cursor", "terminalCursor.foreground", "editorCursor.foreground");
  put("cursorAccent", "terminal.background", "editor.background");
  put("selectionBackground", "terminal.selectionBackground");
  put("black", "terminal.ansiBlack");
  put("red", "terminal.ansiRed");
  put("green", "terminal.ansiGreen");
  put("yellow", "terminal.ansiYellow");
  put("blue", "terminal.ansiBlue");
  put("magenta", "terminal.ansiMagenta");
  put("cyan", "terminal.ansiCyan");
  put("white", "terminal.ansiWhite");
  put("brightBlack", "terminal.ansiBrightBlack");
  put("brightRed", "terminal.ansiBrightRed");
  put("brightGreen", "terminal.ansiBrightGreen");
  put("brightYellow", "terminal.ansiBrightYellow");
  put("brightBlue", "terminal.ansiBrightBlue");
  put("brightMagenta", "terminal.ansiBrightMagenta");
  put("brightCyan", "terminal.ansiBrightCyan");
  put("brightWhite", "terminal.ansiBrightWhite");
  return theme as ITheme;
}
