// Public surface of the theming subsystem (spec §6). Monaco registration lives in ./monaco-theme and is
// never re-exported here, keeping this barrel monaco-free and safe on the first-paint path.

export { applyColorsToCssVars, cssVarName } from "./apply";
export { WEAVIE_DARK, WEAVIE_DARK_ID } from "./builtin/weavie-dark";
export { WEAVIE_LIGHT, WEAVIE_LIGHT_ID } from "./builtin/weavie-light";
export { deriveChromeVars } from "./chrome-vars";
export { type ColorTransform, isHexColor, makeTransform, transformHex } from "./colors";
export {
  applyChromeTheme,
  currentMonacoTheme,
  currentXtermTheme,
  type MonacoThemeUpdate,
  onMonacoThemeChanged,
  onXtermThemeChanged,
} from "./controller";
export {
  type OverrideOp,
  type OverrideTable,
  type ResolvedTheme,
  resolveTheme,
  type SetOp,
  type TransformOp,
} from "./overrides";
export type { SemanticTokenColor, TokenColorRule, VsCodeColorTheme } from "./vscode-theme";
export { paletteToXtermTheme, type XtermTheme } from "./xterm-theme";
