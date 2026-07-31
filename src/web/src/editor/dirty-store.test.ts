import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const owner = {} as ClientSession;

vi.mock("../bridge", () => ({
  registerSessionFeature: () => () => {},
  selectedSession: () => owner,
}));

const { dirtyPaths, dirtyPathsFor, isDirtyPath, setDirtyPath } = await import("./dirty-store");

beforeEach(() => {
  for (const path of dirtyPathsFor(owner)) {
    setDirtyPath(owner, path, false);
  }
});

describe("setDirtyPath", () => {
  it("adds and removes paths from the owning session", () => {
    setDirtyPath(owner, "c:/a.ts", true);
    expect(isDirtyPath("c:/a.ts")).toBe(true);
    setDirtyPath(owner, "c:/a.ts", false);
    expect(isDirtyPath("c:/a.ts")).toBe(false);
  });

  it("matches across path spellings", () => {
    setDirtyPath(owner, "\\home\\user\\a.ts", true);
    expect(isDirtyPath("/home/user/a.ts")).toBe(true);
    setDirtyPath(owner, "C:\\Src\\B.ts", true);
    expect(isDirtyPath("c:/src/b.ts")).toBe(true);
    setDirtyPath(owner, "/home/user/a.ts", false);
    expect(isDirtyPath("\\home\\user\\a.ts")).toBe(false);
  });

  it("does not allocate a new set on a no-op change", () => {
    setDirtyPath(owner, "c:/a.ts", true);
    const reference = dirtyPaths();
    setDirtyPath(owner, "c:/a.ts", true);
    expect(dirtyPaths()).toBe(reference);
    setDirtyPath(owner, "c:/b.ts", false);
    expect(dirtyPaths()).toBe(reference);
  });
});
