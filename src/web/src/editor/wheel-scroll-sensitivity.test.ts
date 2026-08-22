import { describe, expect, it } from "vitest";
import { wheelScrollSensitivity } from "./wheel-scroll-sensitivity";

describe("wheelScrollSensitivity", () => {
  it("normalizes Linux wheel deltas", () => {
    expect(wheelScrollSensitivity(1, "linux")).toBe(5);
  });

  it.each([
    "win",
    "mac",
    "remote",
    undefined,
  ])("leaves the user preference unchanged on %s", (platform) => {
    expect(wheelScrollSensitivity(1, platform)).toBe(1);
  });
});
