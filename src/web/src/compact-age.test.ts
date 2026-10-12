import { describe, expect, it } from "vitest";
import { compactAge } from "./compact-age";

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
