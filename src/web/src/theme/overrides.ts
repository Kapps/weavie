// Theme overrides (spec §6): a sparse, ordered list of declarative ops layered on the active theme's
// color palette. Two op kinds compose freely and apply in order, so "darken all, then set bg pure
// black" leaves the background pure black. Ordered + declarative ⇒ trivial undo (pop), inspect (list),
// and survival across theme switches (transforms re-derive; sets re-apply by key). This module owns the
// schema + resolver + color-math; persistence of the op list is a user setting (owned separately, §6).

import { type ColorTransform, makeTransform, transformHex } from "./colors";

/** Directly set one workbench color key (e.g. <c>editor.background</c> → <c>#000000</c>). Last write wins. */
export interface SetOp {
  kind: "set";
  key: string;
  value: string;
}

/** A parametric op over the whole palette (so the user need not hand-edit hundreds of keys). */
export interface TransformOp {
  kind: "transform";
  op: "darken" | "lighten" | "saturate" | "desaturate" | "contrast";
  /** 0..1 fraction (e.g. 0.2 = "20% darker"). */
  amount: number;
  /** Which keys to affect; only "all" for now (group targeting can extend this later). */
  target?: "all";
}

/** An ordered override op. */
export type OverrideOp = SetOp | TransformOp;

/**
 * Resolves the effective color palette: the base theme's <c>colors</c> map with the override ops
 * applied in order. Pure — no side effects — so it's trivially testable and re-runnable on every
 * change. <c>set</c> writes a key; <c>transform</c> rewrites every hex color perceptually (OKLCH),
 * leaving non-hex values and alpha untouched.
 */
export function resolveColors(
  base: Readonly<Record<string, string>>,
  ops: readonly OverrideOp[],
): Record<string, string> {
  const effective: Record<string, string> = { ...base };
  for (const op of ops) {
    if (op.kind === "set") {
      effective[op.key] = op.value;
      continue;
    }
    const transform: ColorTransform = makeTransform(op.op, op.amount);
    for (const [key, value] of Object.entries(effective)) {
      effective[key] = transformHex(value, transform);
    }
  }
  return effective;
}
