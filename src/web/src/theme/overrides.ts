// Theme overrides (spec §6): a sparse, ordered list of declarative ops layered on the active theme's three
// color tables. The two op kinds compose and apply in order, so "darken all, then set bg pure black"
// leaves the background pure black. Ordered + declarative gives trivial undo (pop), inspect (list), and
// survival across theme switches (transforms re-derive; sets re-apply by key). Owns the schema + resolver
// + color-math; the op list persists in ~/.weavie/theme-overrides.json.

import { type ColorTransform, makeTransform, transformHex } from "./colors";
import type { SemanticTokenColor, TokenColorRule, VsCodeColorTheme } from "./vscode-theme";

/** Which of a theme's three color tables a `set` op targets (default `colors`). */
export type OverrideTable = "colors" | "tokenColors" | "semanticTokenColors";

/**
 * Directly style one entry. Targets the workbench `colors` table by default (e.g. `editor.background`);
 * with `table` set, a syntax table — a TextMate scope in `tokenColors` or a semantic selector in
 * `semanticTokenColors`. Sets a foreground `value`, a `fontStyle` (syntax tables only), or both; at least
 * one is present. Last write wins.
 */
export interface SetOp {
  kind: "set";
  table?: OverrideTable;
  key: string;
  /** Foreground hex. Optional when the op only sets `fontStyle`. */
  value?: string;
  /**
   * Space-separated subset of "italic bold underline strikethrough", or "" to clear inherited styles.
   * Meaningful only on the syntax tables; ignored for `colors`.
   */
  fontStyle?: string;
}

/** Which table(s) a transform sweeps. `syntax` = both syntax tables; `all` = everything (default). */
export type TransformTarget = "all" | "colors" | "tokenColors" | "semanticTokenColors" | "syntax";

/** A parametric op over a chosen slice of the theme's colors, so the user need not hand-edit many keys. */
export interface TransformOp {
  kind: "transform";
  op: "darken" | "lighten" | "saturate" | "desaturate" | "contrast";
  /** 0..1 fraction (e.g. 0.2 = "20% darker"). */
  amount: number;
  /** Which table(s) to affect (default `all`) — so e.g. "saturate the syntax" leaves backgrounds alone. */
  target?: TransformTarget;
}

/** An ordered override op. */
export type OverrideOp = SetOp | TransformOp;

/** The active theme's three color tables after overrides are applied — what the surfaces render from. */
export interface ResolvedTheme {
  colors: Record<string, string>;
  tokenColors: TokenColorRule[];
  semanticTokenColors: Record<string, SemanticTokenColor>;
}

/**
 * Resolves the effective theme: the base theme's three color tables with override ops applied in order.
 * Pure and re-runnable on every change. `set` writes one key in its target table (a `tokenColors` set
 * appends a scope rule that wins by being last); `transform` rewrites every hex across all three tables
 * perceptually (OKLCH), leaving non-hex values and alpha untouched.
 */
export function resolveTheme(base: VsCodeColorTheme, ops: readonly OverrideOp[]): ResolvedTheme {
  const colors: Record<string, string> = { ...base.colors };
  const tokenColors: TokenColorRule[] = base.tokenColors.map((rule) => ({
    ...rule,
    settings: { ...rule.settings },
  }));
  const semanticTokenColors: Record<string, SemanticTokenColor> = {
    ...(base.semanticTokenColors ?? {}),
  };

  for (const op of ops) {
    if (op.kind === "set") {
      applySet(op, colors, tokenColors, semanticTokenColors);
      continue;
    }
    transformTables(
      makeTransform(op.op, op.amount),
      op.target ?? "all",
      colors,
      tokenColors,
      semanticTokenColors,
    );
  }

  return { colors, tokenColors, semanticTokenColors };
}

function applySet(
  op: SetOp,
  colors: Record<string, string>,
  tokenColors: TokenColorRule[],
  semanticTokenColors: Record<string, SemanticTokenColor>,
): void {
  switch (op.table ?? "colors") {
    case "colors":
      // Workbench colors carry no style, so a `fontStyle`-only op here is a no-op.
      if (op.value !== undefined) {
        colors[op.key] = op.value;
      }
      return;
    case "semanticTokenColors": {
      // Merge over the current entry so a style-only op keeps the existing foreground (and vice-versa).
      const prev = semanticTokenColors[op.key];
      const prevForeground = typeof prev === "string" ? prev : prev?.foreground;
      const foreground = op.value ?? prevForeground;
      if (op.fontStyle === undefined) {
        // Color-only op: keep the bare-hex form.
        if (foreground !== undefined) {
          semanticTokenColors[op.key] = foreground;
        }
        return;
      }
      semanticTokenColors[op.key] =
        foreground === undefined
          ? { fontStyle: op.fontStyle }
          : { foreground, fontStyle: op.fontStyle };
      return;
    }
    case "tokenColors": {
      // Append a rule for the exact scope; being last, it wins (TextMate last-rule-wins). A rule omitting
      // `foreground` styles the scope without recoloring it (earlier rules' color stands).
      const settings: TokenColorRule["settings"] = {};
      if (op.value !== undefined) {
        settings.foreground = op.value;
      }
      if (op.fontStyle !== undefined) {
        settings.fontStyle = op.fontStyle;
      }
      tokenColors.push({ name: `override:${op.key}`, scope: op.key, settings });
      return;
    }
  }
}

function transformTables(
  transform: ColorTransform,
  target: TransformTarget,
  colors: Record<string, string>,
  tokenColors: TokenColorRule[],
  semanticTokenColors: Record<string, SemanticTokenColor>,
): void {
  if (target === "all" || target === "colors") {
    for (const [key, value] of Object.entries(colors)) {
      colors[key] = transformHex(value, transform);
    }
  }
  if (target === "all" || target === "syntax" || target === "tokenColors") {
    for (const rule of tokenColors) {
      if (rule.settings.foreground !== undefined) {
        rule.settings.foreground = transformHex(rule.settings.foreground, transform);
      }
    }
  }
  if (target === "all" || target === "syntax" || target === "semanticTokenColors") {
    for (const [key, value] of Object.entries(semanticTokenColors)) {
      if (typeof value === "string") {
        semanticTokenColors[key] = transformHex(value, transform);
      } else if (value.foreground !== undefined) {
        semanticTokenColors[key] = {
          ...value,
          foreground: transformHex(value.foreground, transform),
        };
      }
    }
  }
}
