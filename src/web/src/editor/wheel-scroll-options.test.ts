import { describe, expect, it } from "vitest";
import { editorWheelOptions } from "./wheel-scroll-options";

describe("shared editor wheel options", () => {
  const options = { mouseWheelScrollSensitivity: 3, fastScrollSensitivity: 7 };
  it("normalizes Linux wheel deltas and preserves the Alt multiplier", () => {
    expect(editorWheelOptions(options, "linux")).toEqual({
      mouseWheelScrollSensitivity: 15,
      fastScrollSensitivity: 7,
    });
  });

  it.each([
    "win",
    "mac",
    "remote",
    undefined,
  ])("leaves the user preference unchanged on %s", (platform) => {
    expect(editorWheelOptions(options, platform)).toEqual(options);
  });
});
