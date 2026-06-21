// Color math for theme overrides (spec §6). Transforms operate in OKLCH for perceptually-even results, so
// darkening by 20% looks uniform everywhere unlike naive sRGB/HSL scaling. Alpha is always preserved (many
// VS Code colors are 8-digit hex overlays, and a transform must touch only the color). Inputs/outputs are
// CSS hex strings (#rgb, #rgba, #rrggbb, #rrggbbaa); non-hex values are returned unchanged.

interface Rgba {
  /** Red, green, blue in 0..1 (gamma sRGB). */
  r: number;
  g: number;
  b: number;
  /** Alpha in 0..1. */
  a: number;
}

/** An OKLCH color: perceptual lightness (0..1), chroma (>=0), hue (degrees), plus preserved alpha. */
interface Oklch {
  l: number;
  c: number;
  h: number;
  a: number;
}

const clamp01 = (x: number): number => Math.min(1, Math.max(0, x));

function parseHex(hex: string): Rgba | null {
  const h = /^#([0-9a-fA-F]{3,8})$/.exec(hex.trim())?.[1];
  if (h === undefined) {
    return null;
  }
  const expand = (s: string): number => Number.parseInt(s.length === 1 ? s + s : s, 16) / 255;
  if (h.length === 3 || h.length === 4) {
    return {
      r: expand(h.slice(0, 1)),
      g: expand(h.slice(1, 2)),
      b: expand(h.slice(2, 3)),
      a: h.length === 4 ? expand(h.slice(3, 4)) : 1,
    };
  }
  if (h.length === 6 || h.length === 8) {
    return {
      r: expand(h.slice(0, 2)),
      g: expand(h.slice(2, 4)),
      b: expand(h.slice(4, 6)),
      a: h.length === 8 ? expand(h.slice(6, 8)) : 1,
    };
  }
  return null;
}

function toHex(rgba: Rgba): string {
  const byte = (x: number): string =>
    Math.round(clamp01(x) * 255)
      .toString(16)
      .padStart(2, "0");
  const base = `#${byte(rgba.r)}${byte(rgba.g)}${byte(rgba.b)}`;
  return rgba.a >= 1 ? base : base + byte(rgba.a);
}

// sRGB gamma <-> linear.
const srgbToLinear = (c: number): number =>
  c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
const linearToSrgb = (c: number): number =>
  c <= 0.0031308 ? c * 12.92 : 1.055 * c ** (1 / 2.4) - 0.055;

// sRGB (0..1) -> OKLab -> OKLCH. Matrices from Björn Ottosson's OKLab definition.
function rgbaToOklch(rgba: Rgba): Oklch {
  const r = srgbToLinear(rgba.r);
  const g = srgbToLinear(rgba.g);
  const b = srgbToLinear(rgba.b);

  const l_ = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m_ = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s_ = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);

  const labL = 0.2104542553 * l_ + 0.793617785 * m_ - 0.0040720468 * s_;
  const labA = 1.9779984951 * l_ - 2.428592205 * m_ + 0.4505937099 * s_;
  const labB = 0.0259040371 * l_ + 0.7827717662 * m_ - 0.808675766 * s_;

  const c = Math.hypot(labA, labB);
  let h = (Math.atan2(labB, labA) * 180) / Math.PI;
  if (h < 0) {
    h += 360;
  }
  return { l: labL, c, h, a: rgba.a };
}

// OKLCH -> OKLab -> sRGB (0..1).
function oklchToRgba(color: Oklch): Rgba {
  const hr = (color.h * Math.PI) / 180;
  const labA = color.c * Math.cos(hr);
  const labB = color.c * Math.sin(hr);

  const l_ = (color.l + 0.3963377774 * labA + 0.2158037573 * labB) ** 3;
  const m_ = (color.l - 0.1055613458 * labA - 0.0638541728 * labB) ** 3;
  const s_ = (color.l - 0.0894841775 * labA - 1.291485548 * labB) ** 3;

  const r = 4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_;
  const g = -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_;
  const b = -0.0041960863 * l_ - 0.7034186147 * m_ + 1.707614701 * s_;

  return {
    r: clamp01(linearToSrgb(r)),
    g: clamp01(linearToSrgb(g)),
    b: clamp01(linearToSrgb(b)),
    a: color.a,
  };
}

/** A perceptual transform over a single color, expressed in OKLCH (alpha preserved). */
export type ColorTransform = (color: Oklch) => Oklch;

/** Applies a transform to a hex color, preserving alpha; returns non-hex inputs unchanged. */
export function transformHex(hex: string, transform: ColorTransform): string {
  const rgba = parseHex(hex);
  if (rgba === null) {
    return hex;
  }
  return toHex(oklchToRgba(transform(rgbaToOklch(rgba))));
}

/**
 * Builds a named perceptual transform. `amount` is a 0..1 fraction: darken/lighten move lightness,
 * saturate/desaturate scale chroma, and contrast pushes lightness away from mid-grey.
 */
export function makeTransform(
  op: "darken" | "lighten" | "saturate" | "desaturate" | "contrast",
  amount: number,
): ColorTransform {
  const k = clamp01(amount);
  switch (op) {
    case "darken":
      return (c) => ({ ...c, l: clamp01(c.l * (1 - k)) });
    case "lighten":
      return (c) => ({ ...c, l: clamp01(c.l + (1 - c.l) * k) });
    case "saturate":
      return (c) => ({ ...c, c: c.c * (1 + k) });
    case "desaturate":
      return (c) => ({ ...c, c: c.c * (1 - k) });
    case "contrast":
      return (c) => ({ ...c, l: clamp01(0.5 + (c.l - 0.5) * (1 + k)) });
  }
}

/** True if `value` is a hex color this module can transform. */
export const isHexColor = (value: string): boolean => parseHex(value) !== null;
