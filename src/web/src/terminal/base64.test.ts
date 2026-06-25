import { describe, expect, it } from "vitest";
import { base64ToBytes, bytesToBase64 } from "./base64";

describe("base64 terminal codec", () => {
  it("matches a known vector in both directions", () => {
    expect(bytesToBase64(new Uint8Array([72, 105]))).toBe("SGk=");
    expect([...base64ToBytes("SGk=")]).toEqual([72, 105]);
  });

  it("round-trips the empty buffer", () => {
    expect(bytesToBase64(new Uint8Array([]))).toBe("");
    expect(base64ToBytes("")).toEqual(new Uint8Array([]));
  });

  it("round-trips every possible byte value", () => {
    const all = new Uint8Array(256);
    for (let i = 0; i < 256; i++) {
      all[i] = i;
    }
    expect([...base64ToBytes(bytesToBase64(all))]).toEqual([...all]);
  });

  it("round-trips a buffer larger than the 0x8000 chunking boundary", () => {
    // bytesToBase64 encodes in 0x8000-byte chunks; this exercises the multi-chunk loop and its seam.
    const big = new Uint8Array(0x8000 * 2 + 7);
    for (let i = 0; i < big.length; i++) {
      big[i] = (i * 31 + 7) & 0xff;
    }
    expect(base64ToBytes(bytesToBase64(big))).toEqual(big);
  });
});
