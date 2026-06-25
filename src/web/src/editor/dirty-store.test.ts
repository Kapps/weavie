import { beforeEach, describe, expect, it } from "vitest";
import { dirtyPaths, setDirtyPath } from "./dirty-store";

// Module-global signal; reset to clean between tests.
beforeEach(() => {
  for (const p of [...dirtyPaths()]) {
    setDirtyPath(p, false);
  }
});

describe("setDirtyPath", () => {
  it("adds and removes paths from the dirty set", () => {
    setDirtyPath("c:/a.ts", true);
    expect(dirtyPaths().has("c:/a.ts")).toBe(true);
    setDirtyPath("c:/a.ts", false);
    expect(dirtyPaths().has("c:/a.ts")).toBe(false);
  });

  it("does not allocate a new set on a no-op change (avoids needless re-renders)", () => {
    setDirtyPath("c:/a.ts", true);
    const ref = dirtyPaths();
    setDirtyPath("c:/a.ts", true); // already dirty
    expect(dirtyPaths()).toBe(ref);
    setDirtyPath("c:/b.ts", false); // already clean
    expect(dirtyPaths()).toBe(ref);
  });
});
