import { describe, expect, it } from "vitest";
import { computeDiffLines } from "./diff-computation";

describe("diff computation", () => {
  it("computes sparse and complete rewrites of 5,000-line files", () => {
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
