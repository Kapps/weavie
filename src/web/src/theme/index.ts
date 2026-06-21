// Public surface of the theming subsystem (spec §6): the VS Code theme shape, the override schema +
// resolver + color-math, the built-in themes, and the runtime controller that drives all three render
// surfaces off one resolved palette. Monaco registration lives in ./monaco-theme and is never re-exported
// here, keeping this barrel monaco-free and safe on the first-paint path.

export { isHexColor, makeTransform, transformHex, type ColorTransform } from "./colors";
export {
  resolveTheme,
  type OverrideOp,
  type OverrideTable,
  type ResolvedTheme,
  type SetOp,
  type TransformOp,
} from "./overrides";
export { applyColorsToCssVars, cssVarName } from "./apply";
export { deriveChromeVars } from "./chrome-vars";
export { paletteToXtermTheme, type XtermTheme } from "./xterm-theme";
export { WEAVIE_DARK, WEAVIE_DARK_ID } from "./builtin/weavie-dark";
export { WEAVIE_LIGHT, WEAVIE_LIGHT_ID } from "./builtin/weavie-light";
export type { VsCodeColorTheme, TokenColorRule, SemanticTokenColor } from "./vscode-theme";
export {
  applyChromeTheme,
  currentXtermTheme,
  onXtermThemeChanged,
  currentMonacoTheme,
  onMonacoThemeChanged,
  type MonacoThemeUpdate,
} from "./controller";
