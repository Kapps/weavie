import { afterEach, describe, expect, it, vi } from "vitest";
import { computeDiffLines } from "./diff-computation";

describe("diff computation", () => {
  afterEach(() => vi.useRealTimers());

  // The engine's time boundary reads Date.now(); freezing it makes this assert the result, not the runner's speed.
  // 2026-10-12 flake (https://github.com/Kapps/weavie/actions/runs/38289112149): a loaded CI runner hit the 1s
  // boundary and got null. Fixed by freezing the clock so the boundary can't trip.
  it("computes sparse and complete rewrites of 5,000-line files", () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    const original = Array.from({ length: 5_000 }, (_, index) => `old line ${index}`);
    const sparse = [...original];
    sparse[2_500] = "changed line 2500";
    const rewritten = original.map((_, index) => `new line ${index}`);

    expect(computeDiffLines(original, sparse)).toHaveLength(1);
    expect(computeDiffLines(original, rewritten)).toHaveLength(1);
  });

  it("keeps character-level mappings for focused replacements", () => {
    const changes = computeDiffLines(["const answer = 41;"], ["const answer = 42;"]);

    expect(changes?.[0]?.innerChanges).not.toHaveLength(0);
  });
});
