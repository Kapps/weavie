import { describe, expect, it } from "vitest";
import { briefAge, compactAge } from "./compact-age";

const NOW = Date.UTC(2026, 9, 12, 12, 0);
const ago = (minutes: number): string => compactAge(NOW - minutes * 60_000, NOW);

describe("compactAge", () => {
  it("rounds to the coarsest useful unit", () => {
    expect(ago(0.2)).toBe("just now");
    expect(ago(5)).toBe("5m ago");
    expect(ago(130)).toBe("2h ago");
    expect(ago(3 * 24 * 60)).toBe("3d ago");
  });

  it("shows a date past a month", () => {
    expect(ago(40 * 24 * 60)).toBe(new Date(NOW - 40 * 24 * 60 * 60_000).toLocaleDateString());
  });
});

describe("briefAge", () => {
  const brief = (minutes: number): string => briefAge(NOW - minutes * 60_000, NOW);

  it("drops the suffix for the end of a row", () => {
    expect(brief(0.2)).toBe("now");
    expect(brief(5)).toBe("5m");
    expect(brief(130)).toBe("2h");
    expect(brief(3 * 24 * 60)).toBe("3d");
    expect(brief(40 * 24 * 60)).toBe(
      new Date(NOW - 40 * 24 * 60 * 60_000).toLocaleDateString(undefined, {
        month: "short",
        day: "numeric",
      }),
    );
  });
});
