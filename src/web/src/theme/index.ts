// Public surface of the theming subsystem (spec §6). It owns the override schema, the resolver +
// color-math, and the live application to CSS vars (and, as it grows, xterm + Monaco). Persistence of
// the selected theme and the override op list is a user setting owned elsewhere; the MCP tools that let
// Claude drive overrides plug into the capability registry. This module stays pure + framework-free so
// those layers can call it freely.

export { isHexColor, makeTransform, transformHex, type ColorTransform } from "./colors";
export { resolveColors, type OverrideOp, type SetOp, type TransformOp } from "./overrides";
export { applyColorsToCssVars, cssVarName } from "./apply";
export { DEFAULT_DARK_PALETTE } from "./default-palette";
