// Public surface of the theming subsystem (spec §6). It owns the VS Code theme shape, the override schema
// + resolver + color-math, the built-in themes, and the runtime controller that drives all three render
// surfaces (Monaco, xterm, chrome) off one resolved palette. The Monaco-specific registration lives in
// ./monaco-theme (it pulls in monaco) and is imported directly from the editor chunk — never re-exported
// here, so this barrel stays monaco-free and safe to import on the first-paint path.

export { isHexColor, makeTransform, transformHex, type ColorTransform } from "./colors";
export {
  resolveColors,
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
export type { VsCodeColorTheme, TokenColorRule, SemanticTokenColor } from "./vscode-theme";
export {
  applyChromeTheme,
  currentXtermTheme,
  onXtermThemeChanged,
  currentMonacoTheme,
  onMonacoThemeChanged,
  type MonacoThemeUpdate,
} from "./controller";
