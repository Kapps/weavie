import { describe, expect, it } from "vitest";
import { isHexColor, makeTransform, transformHex } from "./colors";

describe("isHexColor", () => {
  it("accepts 3/4/6/8-digit hex", () => {
    for (const v of ["#abc", "#abcd", "#aabbcc", "#aabbccdd"]) {
      expect(isHexColor(v)).toBe(true);
    }
  });

  it("rejects non-hex and malformed values", () => {
    for (const v of ["rgb(0,0,0)", "transparent", "#ab", "#12345", "abc123", ""]) {
      expect(isHexColor(v)).toBe(false);
    }
  });
});

describe("transformHex", () => {
  it("returns non-hex inputs unchanged", () => {
    const noop = makeTransform("darken", 0.5);
    expect(transformHex("rgb(1,2,3)", noop)).toBe("rgb(1,2,3)");
    expect(transformHex("transparent", noop)).toBe("transparent");
  });

  const channelSum = (hex: string): number =>
    [hex.slice(1, 3), hex.slice(3, 5), hex.slice(5, 7)].reduce(
      (acc, h) => acc + Number.parseInt(h, 16),
      0,
    );

  it("darken lowers overall lightness while preserving alpha", () => {
    const src = "#3aa7ff";
    expect(channelSum(transformHex(src, makeTransform("darken", 0.6)))).toBeLessThan(
      channelSum(src),
    );
    // The alpha byte rides through untouched even as the colour changes.
    expect(transformHex("#3aa7ff80", makeTransform("darken", 0.6))).toMatch(/^#[0-9a-f]{6}80$/);
  });

  it("lighten raises overall lightness", () => {
    const src = "#3aa7ff";
    expect(channelSum(transformHex(src, makeTransform("lighten", 0.6)))).toBeGreaterThan(
      channelSum(src),
    );
  });

  it("desaturate at full strength produces an achromatic (r==g==b) colour", () => {
    const out = transformHex("#3aa7ff", makeTransform("desaturate", 1));
    // Allow a 1-unit rounding wobble across channels from the OKLCH round-trip.
    const rn = Number.parseInt(out.slice(1, 3), 16);
    const gn = Number.parseInt(out.slice(3, 5), 16);
    const bn = Number.parseInt(out.slice(5, 7), 16);
    expect(Math.abs(rn - gn)).toBeLessThanOrEqual(1);
    expect(Math.abs(gn - bn)).toBeLessThanOrEqual(1);
  });

  it("a zero-amount transform is effectively a no-op (within rounding)", () => {
    const out = transformHex("#808080", makeTransform("darken", 0));
    const channels = [out.slice(1, 3), out.slice(3, 5), out.slice(5, 7)].map((h) =>
      Number.parseInt(h, 16),
    );
    for (const c of channels) {
      expect(Math.abs(c - 0x80)).toBeLessThanOrEqual(1);
    }
  });

  it("preserves the alpha byte through a transform", () => {
    expect(transformHex("#11223344", makeTransform("lighten", 0.3))).toMatch(/^#[0-9a-f]{6}44$/);
  });

  it("expands shorthand hex before transforming", () => {
    // #abc -> #aabbcc; a no-op-ish darken should round-trip near that.
    expect(transformHex("#fff", makeTransform("darken", 1))).toBe("#000000");
  });
});
