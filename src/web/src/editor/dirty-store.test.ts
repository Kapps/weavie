import { beforeEach, describe, expect, it } from "vitest";
import { dirtyPaths, isDirtyPath, setDirtyPath } from "./dirty-store";

// Module-global signal; reset to clean between tests.
beforeEach(() => {
  for (const p of [...dirtyPaths()]) {
    setDirtyPath(p, false);
  }
});

describe("setDirtyPath", () => {
  it("adds and removes paths from the dirty set", () => {
    setDirtyPath("c:/a.ts", true);
    expect(isDirtyPath("c:/a.ts")).toBe(true);
    setDirtyPath("c:/a.ts", false);
    expect(isDirtyPath("c:/a.ts")).toBe(false);
  });

  it("matches across path spellings (URI-derived key vs host-native tab path)", () => {
    // The remote-session case: a Windows browser's .fsPath backslashes a Linux host's path.
    setDirtyPath("\\home\\user\\a.ts", true);
    expect(isDirtyPath("/home/user/a.ts")).toBe(true);
    setDirtyPath("C:\\Src\\B.ts", true);
    expect(isDirtyPath("c:/src/b.ts")).toBe(true);
    setDirtyPath("/home/user/a.ts", false);
    expect(isDirtyPath("\\home\\user\\a.ts")).toBe(false);
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
